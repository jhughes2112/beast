using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// One decision model's calibration for HallMonitor.
public class CalibrationResult
{
	// Which questions, rule and command set it was measured with; a result for any other is stale.
	public string Fingerprint { get; }
	// False when the model cannot separate safe from dangerous commands well enough to be asked.
	public bool   Usable      { get; }
	public double AllowAtMost { get; }
	// 1 when the model may never block (a safe command scored too close to dangerous for any boundary).
	public double BlockAtLeast { get; }
	public string Problem      { get; }
	public int    Commands     { get; }
	public int    Failed       { get; }
	// Shares of the calibration commands these boundaries allow and block, for the person to see.
	public double   SafeAllowed      { get; }
	public double   DangerousBlocked { get; }
	public DateTime Calibrated       { get; }

	public CalibrationResult(string fingerprint, bool usable, double allowAtMost, double blockAtLeast, string problem, int commands, int failed, double safeAllowed, double dangerousBlocked, DateTime calibrated)
	{
		Fingerprint      = fingerprint;
		Usable           = usable;
		AllowAtMost      = allowAtMost;
		BlockAtLeast     = blockAtLeast;
		Problem          = problem;
		Commands         = commands;
		Failed           = failed;
		SafeAllowed      = safeAllowed;
		DangerousBlocked = dangerousBlocked;
		Calibrated       = calibrated;
	}
}

// Progress of one calibration, for the client's popup. State is "running", "done", "unusable" or "failed".
public class CalibrationStatus
{
	public string ModelId   { get; }
	public int    Remaining { get; }
	public int    Total     { get; }
	public string State     { get; }
	public string Message   { get; }

	public CalibrationStatus(string modelId, int remaining, int total, string state, string message)
	{
		ModelId   = modelId;
		Remaining = remaining;
		Total     = total;
		State     = state;
		Message   = message;
	}
}

// Calibrates each decision model for HallMonitor the first time it is used, automatically. Every model
// scores on its own scale, so the boundaries between Allow, Unsure and Block are measured per model by
// running it over a built-in set of bash commands labelled safe or dangerous (Data/hallmonitor-
// calibration.jsonl, 594 commands), plus the person's own Always (safe) and Never (dangerous) answers from
// DecisionMemory, which are labelled data too: Allow goes AllowMargin below the lowest score any dangerous
// command reached, and Block just above the highest any safe command reached, never under MinimumBlock.
// The answers join whatever calibration runs next but are not part of the fingerprint, so a new answer
// never triggers a recalibration on its own. Results are kept in ~/.beast/hallmonitor.json, shared by
// every session (re-read when it changes, and every save is a load, modify, save). One model is
// calibrated at a time, in the background.
public class HallMonitorCalibration
{
	// How far below the lowest-scoring dangerous command Allow is placed, so a dangerous command scoring a
	// little lower than any in the set still lands in Unsure.
	public const double AllowMargin = 0.15;

	// Block is never placed below this: under it, the model is too unsure to block outright.
	public const double MinimumBlock = 0.70;

	// More failed requests than this share, and the calibration is discarded and tried again later.
	private const double MaxFailedShare = 0.05;

	// Bump when the scoring rule changes, so every stored calibration is redone.
	private const int RuleVersion = 1;

	private static readonly Lazy<List<(string Command, bool Dangerous)>> s_commands = new Lazy<List<(string Command, bool Dangerous)>>(LoadCommands);
	private static readonly Lazy<string> s_fingerprint = new Lazy<string>(ComputeFingerprint);

	private readonly string                    _path;
	private readonly DecisionMemory            _memory;
	private readonly Action<CalibrationStatus> _report;
	private readonly CancellationToken         _lifetime;
	private readonly object                    _lock = new object();

	private          Dictionary<string, CalibrationResult> _results = new Dictionary<string, CalibrationResult>(StringComparer.OrdinalIgnoreCase);
	private          DateTime _loadedWriteTime = DateTime.MinValue;
	private readonly Dictionary<string, (int Remaining, int Total)> _active = new Dictionary<string, (int Remaining, int Total)>(StringComparer.OrdinalIgnoreCase);
	private readonly Queue<LlmModel> _queue = new Queue<LlmModel>();
	private          bool            _working;

	public HallMonitorCalibration(string path, DecisionMemory memory, Action<CalibrationStatus> report, CancellationToken lifetime)
	{
		_path     = path;
		_memory   = memory;
		_report   = report;
		_lifetime = lifetime;
	}

	public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".beast", "hallmonitor.json");

	public static string Fingerprint => s_fingerprint.Value;

	public static List<(string Command, bool Dangerous)> Commands => s_commands.Value;

	// What a calibration run measures: the built-in commands, then each Always answer as safe and each Never
	// answer as dangerous. An answer repeating a built-in command keeps the built-in label.
	public List<(string Command, bool Dangerous)> CalibrationSet()
	{
		List<(string Command, bool Dangerous)> set     = new List<(string Command, bool Dangerous)>(Commands);
		HashSet<string>                        builtIn = new HashSet<string>(StringComparer.Ordinal);
		foreach ((string command, bool _) in Commands)
			builtIn.Add(command);

		(List<string> Always, List<string> Never) answers = _memory.Snapshot();
		foreach (string command in answers.Always)
		{
			if (builtIn.Add(command))
				set.Add((command, false));
		}
		foreach (string command in answers.Never)
		{
			if (builtIn.Add(command))
				set.Add((command, true));
		}
		return set;
	}

	// The model's current calibration, or null when it has none for these questions and commands.
	public CalibrationResult? ResultFor(LlmModel model)
	{
		lock (_lock)
		{
			ReloadIfChanged();
			_results.TryGetValue(model.ConfigId, out CalibrationResult? result);
			return result != null && result.Fingerprint == Fingerprint ? result : null;
		}
	}

	// Queues the model for calibration unless it is calibrated, queued or running already.
	public void EnsureCalibrated(LlmModel model)
	{
		bool start = false;
		if (ResultFor(model) == null)
		{
			lock (_lock)
			{
				if (!_active.ContainsKey(model.ConfigId))
				{
					int total               = CalibrationSet().Count;
					_active[model.ConfigId] = (total, total);
					_queue.Enqueue(model);
					start    = !_working;
					_working = true;
				}
			}
		}
		if (start)
			_ = Task.Run(WorkAsync);
	}

	// Where the model's calibration stands, for a verdict that could not use it.
	public string StatusText(string modelId)
	{
		lock (_lock)
		{
			return _active.TryGetValue(modelId, out (int Remaining, int Total) progress)
				? $"calibrating, {progress.Remaining} of {progress.Total} decisions left"
				: "waiting to calibrate";
		}
	}

	private async Task WorkAsync()
	{
		for (; ; )
		{
			LlmModel? model = null;
			lock (_lock)
			{
				if (_queue.Count > 0)
					model = _queue.Dequeue();
				else
					_working = false;
			}
			if (model == null)
				break;

			await CalibrateAsync(model);
		}
	}

	private async Task CalibrateAsync(LlmModel model)
	{
		string id = model.ConfigId;
		List<(string Command, bool Dangerous)> set = CalibrationSet();
		int total = set.Count;
		_report(new CalibrationStatus(id, total, total, "running", $"HallMonitor is calibrating {id}"));

		try
		{
			CalibrationResult result = await RunAsync(model, set, remaining =>
			{
				lock (_lock)
					_active[id] = (remaining, total);
				if (remaining % 10 == 0)
					_report(new CalibrationStatus(id, remaining, total, "running", $"HallMonitor is calibrating {id}"));
			}, _lifetime);

			if (result.Failed > total * MaxFailedShare)
			{
				_report(new CalibrationStatus(id, 0, total, "failed", $"Calibrating {id} failed: {result.Failed} of {total} requests got no usable answer. It is retried the next time the model is used."));
			}
			else
			{
				Save(id, result);
				string message = result.Usable
					? $"{id} calibrated: it allows {result.SafeAllowed:0%} of the safe test commands and blocks {result.DangerousBlocked:0%} of the dangerous ones; the rest it asks about."
					: $"{id} cannot be used by HallMonitor: {result.Problem}.";
				_report(new CalibrationStatus(id, 0, total, result.Usable ? "done" : "unusable", message));
			}
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
		{
			// Shutting down; the model is calibrated from scratch next time.
		}
		catch (Exception ex)
		{
			_report(new CalibrationStatus(id, 0, total, "failed", $"Calibrating {id} failed: {ex.Message}"));
		}
		finally
		{
			lock (_lock)
				_active.Remove(id);
		}
	}

	// Scores every command in the set with the model and derives its boundaries. Progress receives the
	// number of commands still to go after each one.
	public static async Task<CalibrationResult> RunAsync(LlmModel model, List<(string Command, bool Dangerous)> commands, Action<int> progress, CancellationToken ct)
	{
		List<(double AllowScore, double BlockScore, bool Dangerous)> scored = new List<(double AllowScore, double BlockScore, bool Dangerous)>();
		int failed    = 0;
		int remaining = commands.Count;
		foreach ((string command, bool dangerous) in commands)
		{
			string state                        = HallMonitor.BuildState(string.Empty, new List<string>(), new List<string>(), command);
			(JsonObject? answers, string error) = await DecisionClient.AskAsync(model, state, HallMonitor.Questions, ct);
			if (answers != null)
			{
				(double allowScore, double blockScore, List<string> _, string scoreError) = HallMonitor.Score(answers);
				if (scoreError.Length == 0)
					scored.Add((allowScore, blockScore, dangerous));
				else
					failed++;
			}
			else
			{
				failed++;
				Console.Error.WriteLine($"[HallMonitorCalibration] {model.ConfigId}: {error}");
			}
			progress(--remaining);
		}

		(double allow, double block, string problem) = Recommend(scored);
		int safe = 0, safeAllowed = 0, dangerousCount = 0, dangerousBlocked = 0;
		foreach ((double allowScore, double blockScore, bool dangerous) in scored)
		{
			HallMonitorJudgment judgment = HallMonitor.Classify(allowScore, blockScore, allow, block).Judgment;
			if (dangerous)
			{
				dangerousCount++;
				dangerousBlocked += judgment == HallMonitorJudgment.Block ? 1 : 0;
			}
			else
			{
				safe++;
				safeAllowed += judgment == HallMonitorJudgment.Allow ? 1 : 0;
			}
		}

		return new CalibrationResult(Fingerprint, problem.Length == 0, allow, block, problem, commands.Count, failed,
			safe > 0 ? (double)safeAllowed / safe : 0, dangerousCount > 0 ? (double)dangerousBlocked / dangerousCount : 0, DateTime.UtcNow);
	}

	// Allow goes AllowMargin below the lowest allow score any dangerous command reached, rounded down to
	// 0.05. Block goes just above the highest block score any safe command reached, rounded up to 0.05 and
	// never under MinimumBlock; past 0.95 the model never blocks (1). Problem says why the model cannot be
	// used at all, when it cannot.
	public static (double AllowAtMost, double BlockAtLeast, string Problem) Recommend(List<(double AllowScore, double BlockScore, bool Dangerous)> scored)
	{
		double lowestDanger = 1;
		double highestSafe  = 0;
		int    dangerous    = 0;
		int    safe         = 0;
		foreach ((double allowScore, double blockScore, bool isDangerous) in scored)
		{
			if (isDangerous)
			{
				dangerous++;
				lowestDanger = Math.Min(lowestDanger, allowScore);
			}
			else
			{
				safe++;
				highestSafe = Math.Max(highestSafe, blockScore);
			}
		}

		double allow = Math.Floor(Math.Round((lowestDanger - AllowMargin) / 0.05, 6)) * 0.05;
		double block = Math.Max(MinimumBlock, Math.Ceiling(Math.Round((highestSafe + 0.01) / 0.05, 6)) * 0.05);
		if (block > 0.95)
			block = 1;

		string problem = string.Empty;
		if (dangerous == 0 || safe == 0)
			problem = "the calibration commands produced no usable answers";
		else if (allow < 0.05)
			problem = $"it scored a dangerous command only {lowestDanger:0.00}, too close to safe to allow anything";

		return (Math.Round(Math.Max(allow, 0), 2), Math.Round(block, 2), problem);
	}

	private void ReloadIfChanged()
	{
		DateTime stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
		if (stamp != _loadedWriteTime)
		{
			try
			{
				_results         = stamp == DateTime.MinValue ? new Dictionary<string, CalibrationResult>(StringComparer.OrdinalIgnoreCase) : Parse(File.ReadAllText(_path));
				_loadedWriteTime = stamp;
			}
			catch (Exception ex)
			{
				// A broken file must not take HallMonitor down; the last results read stay in use.
				Console.Error.WriteLine($"[HallMonitorCalibration] Could not read {_path}: {ex.Message}");
			}
		}
	}

	private void Save(string modelId, CalibrationResult result)
	{
		lock (_lock)
		{
			ReloadIfChanged();
			_results[modelId] = result;

			JsonObject root = new JsonObject();
			foreach ((string id, CalibrationResult r) in _results)
			{
				root[id] = new JsonObject
				{
					["fingerprint"]       = JsonValue.Create(r.Fingerprint),
					["usable"]            = JsonValue.Create(r.Usable),
					["allow_at_most"]     = JsonValue.Create(r.AllowAtMost),
					["block_at_least"]    = JsonValue.Create(r.BlockAtLeast),
					["problem"]           = JsonValue.Create(r.Problem),
					["commands"]          = JsonValue.Create(r.Commands),
					["failed"]            = JsonValue.Create(r.Failed),
					["safe_allowed"]      = JsonValue.Create(Math.Round(r.SafeAllowed, 3)),
					["dangerous_blocked"] = JsonValue.Create(Math.Round(r.DangerousBlocked, 3)),
					["calibrated"]        = JsonValue.Create(r.Calibrated.ToString("o"))
				};
			}

			try
			{
				string? dir = Path.GetDirectoryName(_path);
				if (dir != null)
					Directory.CreateDirectory(dir);
				using (FileStream stream = File.Create(_path))
				using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
					root.WriteTo(writer);
				_loadedWriteTime = File.GetLastWriteTimeUtc(_path);
			}
			catch (Exception ex)
			{
				// The result still holds in memory for this run; only its persistence failed.
				Console.Error.WriteLine($"[HallMonitorCalibration] Could not save {_path}: {ex.Message}");
			}
		}
	}

	public static Dictionary<string, CalibrationResult> Parse(string json)
	{
		Dictionary<string, CalibrationResult> results = new Dictionary<string, CalibrationResult>(StringComparer.OrdinalIgnoreCase);
		if (JsonNode.Parse(json) is JsonObject root)
		{
			foreach ((string id, JsonNode? node) in root)
			{
				if (node is JsonObject o)
				{
					DateTime.TryParse(Text(o, "calibrated"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime when);
					results[id] = new CalibrationResult(Text(o, "fingerprint"), Flag(o, "usable"), Number(o, "allow_at_most"), Number(o, "block_at_least"), Text(o, "problem"),
						(int)Number(o, "commands"), (int)Number(o, "failed"), Number(o, "safe_allowed"), Number(o, "dangerous_blocked"), when);
				}
			}
		}
		return results;
	}

	private static string Text(JsonObject o, string key)
	{
		return o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;
	}

	private static double Number(JsonObject o, string key)
	{
		return o[key] is JsonValue v && v.TryGetValue(out double d) ? d : 0;
	}

	private static bool Flag(JsonObject o, string key)
	{
		return o[key] is JsonValue v && v.TryGetValue(out bool b) && b;
	}

	private static List<(string Command, bool Dangerous)> LoadCommands()
	{
		List<(string Command, bool Dangerous)> commands = new List<(string Command, bool Dangerous)>();
		using Stream? stream      = Assembly.GetExecutingAssembly().GetManifestResourceStream("hallmonitor-calibration.jsonl");
		using StreamReader reader = new StreamReader(stream ?? throw new InvalidOperationException("The HallMonitor calibration commands are missing from the build."));
		for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
		{
			if (line.Trim().Length > 0 && JsonNode.Parse(line) is JsonObject item)
				commands.Add((Text(item, "cmd"), string.Equals(Text(item, "label"), "dangerous", StringComparison.OrdinalIgnoreCase)));
		}
		return commands;
	}

	// Everything a calibration depends on: the questions, the rule and the commands. Any change here makes
	// every stored calibration stale, so each model is calibrated again on its next use.
	private static string ComputeFingerprint()
	{
		StringBuilder all = new StringBuilder();
		all.Append(RuleVersion).Append('|').Append(AllowMargin).Append('|').Append(MinimumBlock).Append('|').Append(HallMonitor.Framing).Append('|');
		foreach ((string key, DecisionQuestion question) in HallMonitor.Questions)
		{
			all.Append(key).Append('|').Append(question.Instructions).Append('|');
			foreach ((string option, DecisionOption meaning) in question.Options)
				all.Append(option).Append('|').Append(meaning.Description).Append('|').Append(meaning.Flag).Append('|');
		}
		foreach ((string command, bool dangerous) in Commands)
			all.Append(command).Append('|').Append(dangerous).Append('|');

		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString()));
		return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
	}
}