using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Runs host_command: a bash command on the user's own machine, executed by the Beast client rather than
// in this container. Every command passes HallMonitor first. Allow runs it, Block refuses it, and Unsure
// does what the user's hallMonitorUnsure setting says (ask them, refuse, or run).
//
// The command runs in one of the shells the client found on its machine (powershell, cmd, bash, sh),
// which it announces on connect; the tool offers and accepts only those, and the client checks again.
//
// Wire protocol (Config frames out, global commands in):
//   client -> agent  /host-shells <name,name,...>                    the shells this machine has
//   agent -> client  {"kind":"host-ask","id","sessionId","shell","command","reason"}  put an Unsure command to the user
//   agent -> client  {"kind":"host-ask-cancel","id"}                 the tool call went away; drop the question
//   client -> agent  /host-answer <id> <always|yes|no|never>
//   agent -> client  {"kind":"host-run","id","shell","command"}      run it
//   agent -> client  {"kind":"host-cancel","id"}                     stop it (timeout or user cancel)
//   client -> agent  /host-output <id> <o|e> <line>                  one line of stdout or stderr
//   client -> agent  /host-exit <id> <exitCode> <message>            finished, after every output line
//
// While it runs, the session streams a Host block (StreamTag.Host): the command first, then each output
// line as it arrives, which the client shows live. The tool's result carries the captured output.
public class HostCommands
{
	public const int DefaultTimeoutSeconds = 120;

	// Upper bound on captured output per stream, as for bash; the rest still streams to the user.
	private const int MaxCapturedChars = 4 * 1024 * 1024;

	// How long a cancelled command gets to report its exit before the tool gives up on it.
	private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(3);

	private sealed class HostRun
	{
		public readonly string        SessionId;
		public readonly StringBuilder StdOut = new StringBuilder();
		public readonly StringBuilder StdErr = new StringBuilder();
		public          bool          Capped;
		public readonly TaskCompletionSource<(int ExitCode, string Message)> Exited = new TaskCompletionSource<(int ExitCode, string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);

		public HostRun(string sessionId)
		{
			SessionId = sessionId;
		}
	}

	private readonly HallMonitor      _monitor;
	private readonly SettingsService  _settings;
	private readonly ITransportServer _transport;
	private readonly object           _lock = new object();

	private readonly Dictionary<string, HostRun> _runs = new Dictionary<string, HostRun>(StringComparer.Ordinal);
	private readonly Dictionary<string, TaskCompletionSource<HumanAnswer>> _asks = new Dictionary<string, TaskCompletionSource<HumanAnswer>>(StringComparer.Ordinal);

	// One host command at a time per session: the client keeps a single live stream per session.
	private readonly Dictionary<string, SemaphoreSlim> _gates = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

	// The shells the client announced; empty until it has, and then host_command is not offered.
	private List<string> _shells = new List<string>();

	public List<string> Shells
	{
		get
		{
			lock (_lock)
				return new List<string>(_shells);
		}
	}

	public void SetShells(string names)
	{
		List<string> shells = new List<string>();
		foreach (string name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!shells.Contains(name))
				shells.Add(name);
		}
		lock (_lock)
			_shells = shells;
	}

	public HostCommands(HallMonitor monitor, SettingsService settings, ITransportServer transport)
	{
		_monitor   = monitor;
		_settings  = settings;
		_transport = transport;
	}

	// The user's setting for Unsure commands; anything unrecognised asks.
	public static UnsurePolicy ParsePolicy(string text)
	{
		UnsurePolicy policy = UnsurePolicy.Ask;
		if (Enum.TryParse(text, true, out UnsurePolicy parsed))
			policy = parsed;
		return policy;
	}

	public async Task<ToolResult> RunAsync(string sessionId, string toolCallId, string shell, string command, int? timeoutSeconds, CancellationToken ct)
	{
		ToolResult   result;
		List<string> shells = Shells;
		if (!shells.Contains(shell))
		{
			string available = shells.Count > 0 ? string.Join(", ", shells) : "none";
			result           = new ToolResult(toolCallId, string.Empty, $"Error: '{shell}' is not a shell available on the host. Available: {available}.", 1, 0);
		}
		else if (string.IsNullOrWhiteSpace(command))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: Command cannot be empty or whitespace.", 1, 0);
		}
		else if (shell == "cmd" && (command.Contains('\n') || command.Contains('\r')))
		{
			result = new ToolResult(toolCallId, string.Empty, "Error: cmd runs a single line. Join the steps with & or &&, or use another shell.", 1, 0);
		}
		else
		{
			SemaphoreSlim gate = GateFor(sessionId);
			await gate.WaitAsync(ct);
			try
			{
				HallMonitorVerdict verdict = await _monitor.CheckAsync(command, ct);
				HallMonitorAction  action  = verdict.ActionFor(ParsePolicy(_settings.Settings.HallMonitorUnsure));
				string             refusal = string.Empty;
				if (action == HallMonitorAction.AskHuman)
				{
					HumanAnswer answer = await AskAsync(sessionId, shell, command, verdict.Reason, ct);
					if (!_monitor.RecordHumanAnswer(command, answer))
						refusal = answer == HumanAnswer.Never
							? "The user refused this command and will refuse it every time. Do not run it or a variant of it again."
							: "The user declined to run this command. Do not retry it or a variant of it; ask them if it is really needed.";
				}
				else if (action == HallMonitorAction.Refuse)
				{
					refusal = verdict.Judgment == HallMonitorJudgment.Block
						? $"HallMonitor blocked this command as dangerous: {verdict.Reason}. Do not retry it or a variant of it; if it is needed, ask the user to run it themselves."
						: $"HallMonitor could not confirm this command is safe ({verdict.Reason}), and doubtful commands are refused here. Do not retry it or a variant of it; if it is needed, ask the user to run it themselves.";
				}

				if (refusal.Length > 0)
					result = new ToolResult(toolCallId, string.Empty, refusal, 1, 0);
				else
					result = await ExecuteAsync(sessionId, toolCallId, shell, command, timeoutSeconds ?? DefaultTimeoutSeconds, ct);
			}
			finally
			{
				gate.Release();
			}
		}
		return result;
	}

	private SemaphoreSlim GateFor(string sessionId)
	{
		lock (_lock)
		{
			if (!_gates.TryGetValue(sessionId, out SemaphoreSlim? gate))
			{
				gate              = new SemaphoreSlim(1, 1);
				_gates[sessionId] = gate;
			}
			return gate;
		}
	}

	// Puts the command to the user and waits for their answer, however long it takes; a cancelled tool
	// call takes the question back off their screen.
	private async Task<HumanAnswer> AskAsync(string sessionId, string shell, string command, string reason, CancellationToken ct)
	{
		string id = Guid.NewGuid().ToString("N");
		TaskCompletionSource<HumanAnswer> answered = new TaskCompletionSource<HumanAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_lock)
			_asks[id] = answered;

		JsonObject frame = new JsonObject
		{
			["kind"]      = "host-ask",
			["id"]        = id,
			["sessionId"] = sessionId,
			["shell"]     = shell,
			["command"]   = command,
			["prompt"]    = HallMonitor.AskText(command),
			["reason"]    = reason
		};
		_transport.Config(sessionId, frame.ToJsonString());

		HumanAnswer answer;
		try
		{
			answer = await answered.Task.WaitAsync(ct);
		}
		catch (OperationCanceledException)
		{
			_transport.Config(sessionId, new JsonObject { ["kind"] = "host-ask-cancel", ["id"] = id }.ToJsonString());
			throw;
		}
		finally
		{
			lock (_lock)
				_asks.Remove(id);
		}
		return answer;
	}

	private async Task<ToolResult> ExecuteAsync(string sessionId, string toolCallId, string shell, string command, int timeoutSeconds, CancellationToken ct)
	{
		string  id  = Guid.NewGuid().ToString("N");
		HostRun run = new HostRun(sessionId);
		lock (_lock)
			_runs[id] = run;

		_transport.StreamStart(sessionId, StreamTag.Host);
		_transport.StreamChunk(sessionId,        command);
		_transport.Config(sessionId, new JsonObject { ["kind"] = "host-run", ["id"] = id, ["shell"] = shell, ["command"] = command }.ToJsonString());

		int    timeout  = timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;
		bool   timedOut = false;
		int    exitCode = 1;
		string message  = string.Empty;
		try
		{
			using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
			limit.CancelAfter(TimeSpan.FromSeconds(timeout));
			try
			{
				(exitCode, message) = await run.Exited.Task.WaitAsync(limit.Token);
			}
			catch (OperationCanceledException)
			{
				// Timeout or user cancel: the client kills the process tree, and its last output still
				// arrives before the exit report, so wait briefly for that.
				_transport.Config(sessionId, new JsonObject { ["kind"] = "host-cancel", ["id"] = id }.ToJsonString());
				try
				{
					(exitCode, message) = await run.Exited.Task.WaitAsync(CancelGrace);
				}
				catch (TimeoutException)
				{
				}
				if (ct.IsCancellationRequested)
					throw;
				timedOut = true;
			}
		}
		finally
		{
			lock (_lock)
				_runs.Remove(id);
			_transport.StreamEnd(sessionId, StreamTag.Host);
		}

		string stdOut;
		string stdErr;
		lock (run)
		{
			stdOut = run.StdOut.ToString().TrimEnd();
			stdErr = run.StdErr.ToString().TrimEnd();
			if (run.Capped)
				stdErr = (stdErr + $"\n[output truncated: exceeded the {MaxCapturedChars / (1024 * 1024)} MB capture limit]").TrimStart();
		}
		if (message.Length > 0)
			stdErr = stdErr.Length > 0 ? stdErr + "\n" + message : message;
		if (timedOut)
		{
			stdErr   = (stdErr + $"\nError: Command timed out after {timeout} seconds.").TrimStart();
			exitCode = exitCode == 0 ? 1 : exitCode;
		}
		return new ToolResult(toolCallId, stdOut, stdErr, exitCode, 0);
	}

	// Handles one /host-... line from the client. Returns false when the content is not one of them.
	// Parsed from the raw content: an output line's own spacing is part of it.
	public bool HandleInbound(string content)
	{
		bool handled = true;
		if (content.StartsWith("/host-shells", StringComparison.Ordinal))
		{
			SetShells(content.Substring("/host-shells".Length));
		}
		else if (content.StartsWith("/host-output ", StringComparison.Ordinal))
		{
			string[] parts = content.Substring("/host-output ".Length).Split(' ', 3);
			if (parts.Length >= 2)
				OnOutput(parts[0], parts[1] == "e", parts.Length == 3 ? parts[2] : string.Empty);
		}
		else if (content.StartsWith("/host-exit ", StringComparison.Ordinal))
		{
			string[] parts = content.Substring("/host-exit ".Length).Split(' ', 3);
			HostRun? run   = null;
			if (parts.Length >= 2 && int.TryParse(parts[1], out int code))
			{
				lock (_lock)
					_runs.TryGetValue(parts[0], out run);
				run?.Exited.TrySetResult((code, parts.Length == 3 ? parts[2] : string.Empty));
			}
		}
		else if (content.StartsWith("/host-answer ", StringComparison.Ordinal))
		{
			string[] parts = content.Substring("/host-answer ".Length).Trim().Split(' ', 2);
			TaskCompletionSource<HumanAnswer>? ask = null;
			if (parts.Length == 2 && Enum.TryParse(parts[1], true, out HumanAnswer answer))
			{
				lock (_lock)
					_asks.TryGetValue(parts[0], out ask);
				ask?.TrySetResult(answer);
			}
		}
		else
		{
			handled = false;
		}
		return handled;
	}

	// Captures the lines for the tool result and passes them straight on to the user's live view. The
	// client batches lines that arrive close together, newline-separated.
	private void OnOutput(string id, bool isError, string lines)
	{
		HostRun? run;
		lock (_lock)
			_runs.TryGetValue(id, out run);
		if (run != null)
		{
			lock (run)
			{
				StringBuilder sink = isError ? run.StdErr : run.StdOut;
				if (sink.Length + lines.Length + 1 <= MaxCapturedChars)
					sink.Append(lines).Append('\n');
				else
					run.Capped = true;
			}
			_transport.StreamChunk(run.SessionId, "\n" + lines);
		}
	}
}