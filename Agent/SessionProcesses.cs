using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// Every process a session's shell starts lives in a PID namespace of the session's own, so none outlives
// the session's work: a background job, a dev server, a headless browser a script left behind. Nothing
// started inside can leave it — nohup, setsid and a double fork all stay in — and when the namespace's
// first process dies the kernel kills every other one in it. So each session has a holder (unshare --pid
// --fork running sleep as the namespace's first process), every shell command enters it with nsenter, and
// ending the session's work kills the holder.
//
// Background jobs within the work are left alone: models run test suites with & or nohup and poll them
// across calls. Instead each command's result names what is still running from earlier ones (by command
// only: no process ids or times, which would make a seeded run's text differ), so the model can stop them
// or knows they are still going.
//
// A compaction successor carries on the predecessor's work, so it takes over the predecessor's namespace
// (Inherit) rather than the predecessor's ending. A session whose holder could not start (a container
// without SYS_ADMIN, or no sandbox at all) runs its commands as before, outside any namespace.
public static class SessionProcesses
{
	private sealed class Holder
	{
		public Process Unshare = null!;
		public int     InitPid;
	}

	// Holders by the session that owns them now; a successor takes its predecessor's.
	private static readonly Dictionary<string, Holder> _holders = new Dictionary<string, Holder>(StringComparer.Ordinal);
	private static readonly SemaphoreSlim              _gate    = new SemaphoreSlim(1, 1);
	private static          bool                       _unavailable;

	// How long a new holder may take to show its first process, and how often it is looked for.
	private static readonly TimeSpan kStartTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan kStartPoll    = TimeSpan.FromMilliseconds(20);

	// Leftovers named in one note before the rest are counted, and the most of a command line shown.
	private const int kMaxListed       = 8;
	private const int kMaxCommandChars = 120;

	// The process to enter for this session's command (the namespace's first process), starting the
	// holder on first use; 0 when the session has none and the command runs outside any namespace.
	public static async Task<int> EnterAsync(string sessionId, CancellationToken ct)
	{
		int init = 0;
		if (sessionId.Length > 0 && Sandbox.ShellUserActive && OperatingSystem.IsLinux())
		{
			await _gate.WaitAsync(ct);
			try
			{
				if (_holders.TryGetValue(sessionId, out Holder? holder) && !holder.Unshare.HasExited)
				{
					init = holder.InitPid;
				}
				else if (!_unavailable)
				{
					Holder? started = await StartAsync(ct);
					if (started != null)
					{
						_holders[sessionId] = started;
						init                = started.InitPid;
					}
				}
			}
			finally
			{
				_gate.Release();
			}
		}
		return init;
	}

	// A compaction successor takes over its predecessor's namespace, with whatever is running in it.
	public static void Inherit(string successorId, string predecessorId)
	{
		_gate.Wait();
		try
		{
			if (_holders.Remove(predecessorId, out Holder? holder))
			{
				_holders[successorId] = holder;
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	// The session's work is over: everything it started is killed with its namespace.
	public static void End(string sessionId)
	{
		Holder? holder;
		_gate.Wait();
		try
		{
			_holders.Remove(sessionId, out holder);
		}
		finally
		{
			_gate.Release();
		}

		if (holder != null)
		{
			try
			{
				// The namespace's first process dying takes every other process in it down with it.
				Process.GetProcessById(holder.InitPid).Kill();
				holder.Unshare.WaitForExit(2000);
			}
			catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
			{
				// Already gone.
			}
			holder.Unshare.Dispose();
		}
	}

	// A line naming what is still running from this session's earlier commands, or "" when nothing is.
	// Only the jobs a finished command left behind are named (those the namespace took in as its own
	// children), not what they started in turn, sorted and by command alone so the line repeats.
	public static string StillRunningNote(string sessionId)
	{
		Holder? holder;
		_gate.Wait();
		try
		{
			_holders.TryGetValue(sessionId, out holder);
		}
		finally
		{
			_gate.Release();
		}

		string note = string.Empty;
		if (holder != null)
		{
			List<string> commands = LeftoverCommands(holder);
			if (commands.Count > 0)
			{
				commands.Sort(StringComparer.Ordinal);
				List<string> shown = new List<string>();
				for (int i = 0; i < commands.Count && i < kMaxListed; i++)
				{
					shown.Add($"`{commands[i]}`");
				}

				string more = commands.Count > kMaxListed ? $" and {commands.Count - kMaxListed} more" : string.Empty;
				note        = $"[Still running from earlier commands: {string.Join(", ", shown)}{more}. Everything this session started is stopped when its work ends; kill any you no longer need.]";
			}
		}
		return note;
	}

	private static async Task<Holder?> StartAsync(CancellationToken ct)
	{
		Holder? holder  = null;
		Process unshare = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName               = "unshare",
				ArgumentList           = { "--pid", "--fork", "--kill-child", "sleep", "infinity" },
				UseShellExecute        = false,
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				CreateNoWindow         = true
			}
		};

		try
		{
			unshare.Start();
			int      init     = 0;
			DateTime deadline = DateTime.UtcNow + kStartTimeout;
			while (init == 0 && !unshare.HasExited && DateTime.UtcNow < deadline)
			{
				init = ChildOf(unshare.Id);
				if (init == 0)
				{
					await Task.Delay(kStartPoll, ct);
				}
			}

			if (init > 0)
			{
				holder = new Holder { Unshare = unshare, InitPid = init };
			}
			else
			{
				string why = unshare.HasExited ? (await unshare.StandardError.ReadToEndAsync(ct)).Trim() : "no first process appeared";
				Console.Error.WriteLine($"[SessionProcesses] No PID namespaces in this container ({why}); shell commands run outside one.");
				_unavailable = true;
				if (!unshare.HasExited)
				{
					unshare.Kill(true);
				}
				unshare.Dispose();
			}
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			Console.Error.WriteLine($"[SessionProcesses] unshare could not be started ({ex.Message}); shell commands run outside a namespace.");
			_unavailable = true;
			unshare.Dispose();
		}
		return holder;
	}

	// The commands of the namespace's own children (jobs a finished command left behind), zombies aside. Only
	// a process inside the namespace can have its first process as a parent, so the parent alone says so:
	// the namespace link of another user's process is not readable without CAP_SYS_PTRACE, which Docker
	// does not grant.
	private static List<string> LeftoverCommands(Holder holder)
	{
		List<string> commands = new List<string>();
		foreach (string dir in Directory.GetDirectories("/proc"))
		{
			if (int.TryParse(Path.GetFileName(dir), out int pid))
			{
				(char state, int parent) = StateOf(pid);
				string command           = parent == holder.InitPid && state != 'Z' ? CommandOf(pid) : string.Empty;
				if (command.Length > 0)
				{
					commands.Add(command);
				}
			}
		}
		return commands;
	}

	// The first child of a process, 0 when it has none (yet).
	private static int ChildOf(int parentPid)
	{
		int child = 0;
		foreach (string dir in Directory.GetDirectories("/proc"))
		{
			if (child == 0 && int.TryParse(Path.GetFileName(dir), out int pid) && StateOf(pid).Parent == parentPid)
			{
				child = pid;
			}
		}
		return child;
	}

	// The state letter and parent from /proc/<pid>/stat; the command name in it may hold spaces or
	// parentheses, so fields are read after its last ')'.
	private static (char State, int Parent) StateOf(int pid)
	{
		char state  = '?';
		int  parent = 0;
		try
		{
			string stat  = File.ReadAllText($"/proc/{pid}/stat");
			int    close = stat.LastIndexOf(')');
			if (close > 0)
			{
				string[] fields = stat.Substring(close + 2).Split(' ');
				if (fields.Length > 1)
				{
					state = fields[0].Length > 0 ? fields[0][0] : '?';
					int.TryParse(fields[1], out parent);
				}
			}
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
		}
		return (state, parent);
	}

	private static string CommandOf(int pid)
	{
		string command = string.Empty;
		try
		{
			command = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim();
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
		}
		return command.Length > kMaxCommandChars ? $"{command.Substring(0, kMaxCommandChars)}…" : command;
	}
}