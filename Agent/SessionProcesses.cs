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

	// A command started with run_in_background. Owner is the session it reports to: a compaction successor
	// takes it over, so the notice reaches whichever session is carrying on the work.
	private sealed class Job
	{
		public Session  Owner = null!;
		public int      Number;
		public Process  Process = null!;
		public string   Command = string.Empty;
		public string   Log     = string.Empty;
		public string   Status  = string.Empty;
		public int      Limit;
		public DateTime Deadline;
	}

	// How often the running jobs' status files are looked at.
	private static readonly TimeSpan kPollInterval = TimeSpan.FromSeconds(1);
	private static          bool     _polling;

	// Holders, running jobs and job counts by the session that owns them now; a successor takes its
	// predecessor's. Jobs are numbered per session from 1, so a seeded run numbers them the same way.
	private static readonly Dictionary<string, Holder>    _holders = new Dictionary<string, Holder>(StringComparer.Ordinal);
	private static readonly Dictionary<string, List<Job>> _jobs    = new Dictionary<string, List<Job>>(StringComparer.Ordinal);
	private static readonly Dictionary<string, int>       _counts  = new Dictionary<string, int>(StringComparer.Ordinal);
	private static readonly SemaphoreSlim                 _gate    = new SemaphoreSlim(1, 1);
	private static          bool _unavailable;

	// The most of a finished job's output its notice carries; the rest is in its log.
	private const int kNoticeTailLines = 20;
	private const int kNoticeTailChars = 2000;

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

	// A compaction successor takes over its predecessor's namespace, with whatever is running in it, its
	// background jobs (whose notices now go to it) and its job count.
	public static void Inherit(Session successor, string predecessorId)
	{
		_gate.Wait();
		try
		{
			if (_holders.Remove(predecessorId, out Holder? holder))
			{
				_holders[successor.Id] = holder;
			}

			if (_jobs.Remove(predecessorId, out List<Job>? jobs))
			{
				foreach (Job job in jobs)
				{
					job.Owner = successor;
				}
				_jobs[successor.Id] = jobs;
			}

			if (_counts.Remove(predecessorId, out int count))
			{
				_counts[successor.Id] = count;
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	// The number the session's next background job gets.
	public static int NextJob(string sessionId)
	{
		int next;
		_gate.Wait();
		try
		{
			next               = (_counts.TryGetValue(sessionId, out int count) ? count : 0) + 1;
			_counts[sessionId] = next;
		}
		finally
		{
			_gate.Release();
		}
		return next;
	}

	// Whether the session has background jobs still running, which it may wait on rather than be prodded.
	public static bool HasRunningJobs(string sessionId)
	{
		bool running;
		_gate.Wait();
		try
		{
			running = _jobs.TryGetValue(sessionId, out List<Job>? jobs) && jobs.Count > 0;
		}
		finally
		{
			_gate.Release();
		}
		return running;
	}

	// The shell text a background command runs as: its output to the log, the command in a subshell (so an
	// exit inside it still reaches the next line), then its exit code to the status file, renamed into place
	// so the file is never read half-written. The status file is how the job says it is done.
	public static string Wrap(string command, string log, string status)
	{
		string q = Quote(log);
		string s = Quote(status);
		return $"exec > {q} 2>&1\n(\n{command}\n)\necho \"$?\" > {s}.tmp && mv {s}.tmp {s}\n";
	}

	private static string Quote(string path)
	{
		return $"'{path.Replace("'", "'\\''")}'";
	}

	// Starts a background job and has it watched: once a second the status files of the running jobs are
	// looked at. A job whose file has appeared is done, with the exit code it wrote; one past its limit is
	// stopped; one whose process is gone without a file (killed) has an unknown code. Each way its owner gets
	// a notice with the end of its output, and wakes if it was waiting. Anything a job leaves behind belongs
	// to the namespace, which names it after later commands and kills it with the session's work.
	public static void Watch(Session owner, int number, ProcessStartInfo psi, string command, string log, string status, int limitSeconds)
	{
		Process process = new Process { StartInfo = psi };
		process.Start();
		Job  job   = new Job { Owner = owner, Number = number, Process = process, Command = command, Log = log, Status = status, Limit = limitSeconds, Deadline = DateTime.UtcNow.AddSeconds(limitSeconds) };
		bool start = false;
		_gate.Wait();
		try
		{
			if (!_jobs.TryGetValue(owner.Id, out List<Job>? jobs))
			{
				jobs            = new List<Job>();
				_jobs[owner.Id] = jobs;
			}
			jobs.Add(job);
			start    = !_polling;
			_polling = true;
		}
		finally
		{
			_gate.Release();
		}

		if (start)
		{
			_ = PollAsync();
		}
	}

	// Runs while any job does: one look at every running job per interval, then the notices, outside the lock.
	private static async Task PollAsync()
	{
		bool more = true;
		while (more)
		{
			await Task.Delay(kPollInterval);
			List<(Session Owner, string Notice)> notices = new List<(Session Owner, string Notice)>();
			List<Job> ended = new List<Job>();
			_gate.Wait();
			try
			{
				foreach ((string _, List<Job> jobs) in _jobs)
				{
					for (int i = jobs.Count - 1; i >= 0; i--)
					{
						string notice = Check(jobs[i]);
						if (notice.Length > 0)
						{
							notices.Add((jobs[i].Owner, notice));
							ended.Add(jobs[i]);
							jobs.RemoveAt(i);
						}
					}
				}

				more = false;
				foreach ((string _, List<Job> jobs) in _jobs)
				{
					more = more || jobs.Count > 0;
				}
				_polling = more;
			}
			finally
			{
				_gate.Release();
			}

			foreach ((Session owner, string notice) in notices)
			{
				owner.AddUserMessage(notice);
			}
			foreach (Job job in ended)
			{
				job.Process.Dispose();
			}
		}
	}

	// The notice for a job that has ended, or "" while it is still running. No times or process ids: only
	// what the job was and how it ended.
	private static string Check(Job job)
	{
		string how = string.Empty;
		if (File.Exists(job.Status))
		{
			how = $"finished with exit code {ReadStatus(job.Status)}";
		}
		else if (DateTime.UtcNow >= job.Deadline)
		{
			Stop(job);
			how = $"was still running at its {job.Limit} second limit and was stopped";
		}
		else if (job.Process.HasExited)
		{
			how = "ended without reporting an exit code (it was killed)";
		}

		string notice = string.Empty;
		if (how.Length > 0)
		{
			string tail   = Tail(job.Log);
			string output = tail.Length > 0 ? $"\nLast of its output:\n{tail}" : "\nIt wrote no output.";
			notice        = $"[Beast: background job {job.Number} (`{FirstLine(job.Command)}`) {how}. Full output: {job.Log}]{output}";
		}
		return notice;
	}

	private static string ReadStatus(string status)
	{
		string code = "unknown";
		try
		{
			string text = File.ReadAllText(status).Trim();
			code        = text.Length > 0 ? text : code;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
		}
		return code;
	}

	private static void Stop(Job job)
	{
		try
		{
			job.Process.Kill(true);
		}
		catch (InvalidOperationException)
		{
			// It finished as the limit came.
		}
	}

	private static string FirstLine(string text)
	{
		string trimmed = text.Trim();
		int    newline = trimmed.IndexOf('\n');
		string first   = newline >= 0 ? $"{trimmed.Substring(0, newline).TrimEnd()} …" : trimmed;
		return first.Length > kMaxCommandChars ? $"{first.Substring(0, kMaxCommandChars)}…" : first;
	}

	// The last lines of a job's log, bounded in lines and characters.
	private static string Tail(string log)
	{
		string tail = string.Empty;
		try
		{
			string[] lines = File.ReadAllText(log).TrimEnd().Split('\n');
			int      start = Math.Max(0, lines.Length - kNoticeTailLines);
			tail           = string.Join("\n", lines, start, lines.Length - start);
			if (tail.Length > kNoticeTailChars)
			{
				tail = $"…{tail.Substring(tail.Length - kNoticeTailChars)}";
			}
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
		}
		return tail;
	}

	// The session's work is over: everything it started is killed with its namespace, background jobs
	// included, and their notices are dropped, since there is no work left for them to report to.
	public static void End(string sessionId)
	{
		Holder?    holder;
		List<Job>? jobs;
		_gate.Wait();
		try
		{
			_holders.Remove(sessionId, out holder);
			_jobs.Remove(sessionId, out jobs);
			_counts.Remove(sessionId);
		}
		finally
		{
			_gate.Release();
		}

		// Taken off the running list above, so no notice comes for them. Outside a namespace (none could be
		// made) killing the holder would reach nothing, so each job is stopped itself.
		if (jobs != null)
		{
			foreach (Job job in jobs)
			{
				Stop(job);
				job.Process.Dispose();
			}
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
		Holder?      holder;
		List<string> jobs = new List<string>();
		_gate.Wait();
		try
		{
			_holders.TryGetValue(sessionId, out holder);
			if (_jobs.TryGetValue(sessionId, out List<Job>? running))
			{
				foreach (Job job in running)
				{
					jobs.Add($"{job.Number} (`{FirstLine(job.Command)}`)");
				}
			}
		}
		finally
		{
			_gate.Release();
		}

		List<string> lines = new List<string>();
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
				lines.Add($"[Still running from earlier commands: {string.Join(", ", shown)}{more}. Everything this session started is stopped when its work ends; kill any you no longer need.]");
			}
		}

		if (jobs.Count > 0)
		{
			lines.Add($"[Background jobs still running: {string.Join(", ", jobs)}. A message comes when each one ends.]");
		}
		return string.Join("\n", lines);
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