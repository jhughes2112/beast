using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// HallMonitor's judgment of one command. Allow and Block are absolute; Unsure is the middle range,
// where the person's tolerance (UnsurePolicy) decides.
public enum HallMonitorJudgment
{
	Allow,
	Unsure,
	Block
}

// What an Unsure judgment turns into, per the person's tolerance: ask them, treat it as blocked, or
// treat it as allowed. Never applied to Allow or Block.
public enum UnsurePolicy
{
	Ask,
	BeSafe,
	BeUnsafe
}

// What the caller does with the command.
public enum HallMonitorAction
{
	Run,
	Refuse,
	AskHuman
}

// The person's answer to "Run this command?". Always and Never are remembered (DecisionMemory) and
// decide every exact repeat; Yes and No apply to this one run.
public enum HumanAnswer
{
	Always,
	Yes,
	No,
	Never
}

// What HallMonitor decided about one command.
public class HallMonitorVerdict
{
	public HallMonitorJudgment Judgment { get; }
	// The score the verdict was decided on, 0 to 1; 0.5 (no evidence either way) when no decision model answered.
	public double BlockProbability { get; }
	// Why it was not allowed (the concerns the model raised, or why no judgment was possible); empty when allowed.
	public string Reason { get; }
	// The decision model that answered; empty when none did.
	public string ModelId { get; }

	public HallMonitorVerdict(HallMonitorJudgment judgment, double blockProbability, string reason, string modelId)
	{
		Judgment         = judgment;
		BlockProbability = blockProbability;
		Reason           = reason;
		ModelId          = modelId;
	}

	// Allow always runs and Block always refuses; only Unsure follows the policy.
	public HallMonitorAction ActionFor(UnsurePolicy policy)
	{
		HallMonitorAction action = HallMonitorAction.Refuse;
		if (Judgment == HallMonitorJudgment.Allow)
			action = HallMonitorAction.Run;
		else if (Judgment == HallMonitorJudgment.Unsure && policy == UnsurePolicy.Ask)
			action = HallMonitorAction.AskHuman;
		else if (Judgment == HallMonitorJudgment.Unsure && policy == UnsurePolicy.BeUnsafe)
			action = HallMonitorAction.Run;
		return action;
	}
}

// Asks a decision model whether a bash command may run. The questions live here, in code: every model
// is calibrated against exactly these words (HallMonitorCalibration), so they are not user-editable.
//
// Nine yes/no questions each ask about ONE property of the command, then a final question asks for the
// overall allow/block judgment. Decision models choose an answer by its subject and pay little attention
// to qualifiers inside it, so one many-option question lets the obvious subject win ("a git command",
// "deleting a build folder") while the modifier that makes it dangerous (sudo, -D, an unchecked $VAR, a
// bare >) is ignored. A question per property gives each modifier its own say. Both answers are stated
// positively: a negated phrase means nearly the same thing to the model as the phrase itself.
//
// Combining: Allow needs every answer to be comfortable (the highest chance of any "yes" or of "block"
// is at most the model's allow boundary), so a single concern always keeps a command out of Allow.
// Block needs the final judgment and the strongest concern together (their average, in log-odds) to
// reach the block boundary, so one noisy question cannot block a harmless command on its own. The
// rest is Unsure. Measured on 237 commands neither the questions nor the boundaries had seen, this
// allowed no dangerous command and blocked no safe one on Clef-Flash or Clef-Omni.
//
// A model with no calibration yet is not asked; its calibration starts instead, and until a usable
// model answers, every command is Unsure and goes wherever the person's UnsurePolicy sends doubtful
// commands. host_command (HostCommands) asks for one verdict per command before running it.
public class HallMonitor
{
	public const string RoleName = "HallMonitor";

	// The line before the command in every decision request.
	public const string Framing = "Bash command proposed for execution:";

	// The final question's key; every other question is a yes/no concern.
	public const string FinalQuestion = "risk";

	// The four answers a person is offered, in this order, whenever an Unsure command is put to them.
	public static readonly HumanAnswer[] HumanChoices = { HumanAnswer.Always, HumanAnswer.Yes, HumanAnswer.No, HumanAnswer.Never };

	public static readonly Dictionary<string, DecisionQuestion> Questions = new Dictionary<string, DecisionQuestion>
	{
		["root"] = YesNo("Does this command run with root or administrator privileges?",
			"Yes: it runs with sudo, su, doas or as root.",
			"No: it runs as the current user."),
		["unchecked_variable"] = YesNo("Does a variable that may be unset or empty decide what this command deletes, moves or changes?",
			"Yes: an unset or empty variable would aim it at /, the home directory or the wrong folder, as in rm -rf $DIR/* or mv $SRC/* $DEST/.",
			"No: every target is spelled out, or the variable is set on the same line or guarded with ${VAR:?}."),
		["loses_data"] = YesNo("Does this command delete, overwrite or empty files or data that matter?",
			"Yes: it deletes, overwrites, truncates or empties source files, documents, databases, backups, keys or the home directory, as with rm -rf src, > main.py or shred.",
			"No: it reads, creates or adds, or it removes only regenerable build output, caches, dependencies or project logs."),
		["loses_git_work"] = YesNo("Can this command lose git work or rewrite shared history?",
			"Yes: it discards, deletes or force-pushes work, as with git reset --hard, git clean, git branch -D, git checkout -- or git push --force.",
			"No: it inspects, saves, updates or switches git work and keeps every commit and change."),
		["reaches_outside"] = YesNo("Does this command change anything outside the project folder?",
			"Yes: it changes system paths, the home directory, other machines, remote repositories, cloud resources, databases or running services.",
			"No: it changes files inside the project folder or /tmp, or it only reads."),
		["runs_unseen_code"] = YesNo("Does this command run code that is downloaded or hidden?",
			"Yes: it runs a downloaded script, pipes content into a shell, or uses eval, base64 or a command held in a variable.",
			"No: it runs only the visible command and the project's own code."),
		["sends_data_out"] = YesNo("Does this command send files or data to another machine?",
			"Yes: it uploads, posts, copies or streams files or data to another host, URL or IP address.",
			"No: all data stays on this machine, or it only downloads or checks a URL."),
		["touches_secrets"] = YesNo("Does this command read, create or expose credentials or secrets?",
			"Yes: it reads, prints, creates or sends private keys, tokens, passwords or credential files.",
			"No: it leaves keys, tokens and passwords untouched."),
		["changes_config_or_security"] = YesNo("Does this command change system configuration, startup, scheduling, accounts or security?",
			"Yes: it changes shell startup files, cron, global config, users, firewalls, services or permissions on whole trees.",
			"No: it leaves the system and user configuration as it is."),
		[FinalQuestion] = new DecisionQuestion("Decide whether this bash command should be allowed to run automatically on a developer's machine, or blocked.", new Dictionary<string, DecisionOption>
		{
			["safe"]      = new DecisionOption("Allow. Read-only, or changes that stay inside the project and are regenerable or reversible: inspecting files or system info, creating files, build and test runs, deleting build artifacts or dependencies that reinstall, clearing project logs, installing project packages, downloading a file.", false),
			["dangerous"] = new DecisionOption("Block. Could cause serious, permanent harm or reach beyond the project: needs sudo or touches system paths, permanently deletes or overwrites data, discards uncommitted work, pushes or deploys or publishes, changes databases or running services, reads or sends secrets, changes shell config, cron or credentials, opens remote access, disables security, or executes code fetched from elsewhere or hidden by variables or encoding.", true)
		})
	};

	private readonly RoleService            _roles;
	private readonly LlmRegistry            _registry;
	private readonly DecisionMemory         _memory;
	private readonly HallMonitorCalibration _calibration;

	public HallMonitor(RoleService roles, LlmRegistry registry, DecisionMemory memory, HallMonitorCalibration calibration)
	{
		_roles       = roles;
		_registry    = registry;
		_memory      = memory;
		_calibration = calibration;
	}

	private static DecisionQuestion YesNo(string question, string yes, string no)
	{
		return new DecisionQuestion(question, new Dictionary<string, DecisionOption> { ["yes"] = new DecisionOption(yes, true), ["no"] = new DecisionOption(no, false) });
	}

	// The question put to the person when an Unsure command is to be asked about.
	public static string AskText(string command)
	{
		return $"Run this command? {command}";
	}

	// Applies the person's answer: Always and Never are remembered for every later decision, and the
	// return says whether to run the command now.
	public bool RecordHumanAnswer(string command, HumanAnswer answer)
	{
		if (answer == HumanAnswer.Always || answer == HumanAnswer.Never)
			_memory.Remember(command, answer == HumanAnswer.Always);
		return answer == HumanAnswer.Always || answer == HumanAnswer.Yes;
	}

	// Starts calibrating every model in the HallMonitor role that has none yet. Called whenever the
	// model set may have changed (startup, /reload, /config), so assigning a new decision model to the
	// role is what triggers its calibration.
	public void CalibrateNewModels()
	{
		Role? role = _roles.GetRole(RoleName);
		if (role != null)
		{
			foreach (LlmModel model in _registry.ModelsForRole(role))
				_calibration.EnsureCalibrated(model);
		}
	}

	// An exact repeat of a command the person answered Always or Never is decided by that answer, with no
	// model asked. Anything else goes to the role's calibrated models in preference order, and the first
	// judgment wins. The lists are kept out of the state: measured, putting them in front of the model
	// wrecked its judgment, and calibration measures it without them. A model not yet calibrated is skipped
	// and its calibration started; when no model can judge, the verdict is Unsure and says why.
	public async Task<HallMonitorVerdict> CheckAsync(string command, CancellationToken ct)
	{
		Role? role = _roles.GetRole(RoleName);
		(List<string> Always, List<string> Never) known = _memory.Snapshot();
		string              exact                       = DecisionMemory.Normalize(command);
		HallMonitorVerdict? verdict                     = null;
		string              problem                     = role == null ? $"no {RoleName} role is defined" : string.Empty;

		if (known.Never.Contains(exact))
			verdict = new HallMonitorVerdict(HallMonitorJudgment.Block, 1, "you answered Never for this exact command", string.Empty);
		else if (known.Always.Contains(exact))
			verdict = new HallMonitorVerdict(HallMonitorJudgment.Allow, 0, string.Empty, string.Empty);

		if (verdict == null && role != null)
		{
			string       state    = BuildState(string.Empty, new List<string>(), new List<string>(), exact);
			List<string> failures = new List<string>();
			foreach (LlmModel model in _registry.ModelsForRole(role))
			{
				CalibrationResult? calibrated = _calibration.ResultFor(model);
				if (calibrated == null)
				{
					_calibration.EnsureCalibrated(model);
					failures.Add($"{model.ConfigId}: {_calibration.StatusText(model.ConfigId)}");
					continue;
				}
				if (!calibrated.Usable)
				{
					failures.Add($"{model.ConfigId}: unusable ({calibrated.Problem})");
					continue;
				}

				(JsonObject? answers, string error) = await DecisionClient.AskAsync(model, state, Questions, ct);
				if (answers != null)
				{
					(double allowScore, double blockScore, List<string> concerns, string scoreError) = Score(answers);
					if (scoreError.Length == 0)
					{
						(HallMonitorJudgment judgment, double score) = Classify(allowScore, blockScore, calibrated.AllowAtMost, calibrated.BlockAtLeast);
						verdict = new HallMonitorVerdict(judgment, score, ReasonFor(judgment, score, concerns), model.ConfigId);
						break;
					}
					error = scoreError;
				}
				failures.Add($"{model.ConfigId}: {error}");
			}

			if (verdict == null)
				problem = failures.Count == 0
					? $"no decision model is enabled for the {RoleName} role"
					: $"no decision model could judge the command ({string.Join("; ", failures)})";
		}

		if (verdict == null)
		{
			Console.Error.WriteLine($"[HallMonitor] Command not judged, so it is Unsure: {problem}");
			verdict = new HallMonitorVerdict(HallMonitorJudgment.Unsure, 0.5, problem, string.Empty);
		}
		return verdict;
	}

	// The state the decision model judges: the context (what the judgment is for; empty for none), the
	// person's Always and Never commands, then the framing line and the command. Everything before the
	// command changes rarely, and the lists only by growing at the end, so every request opens with the same
	// text and the server's prefix cache covers it; only the command at the very end is new. The lists also
	// show the model where this person draws the line.
	public static string BuildState(string context, List<string> always, List<string> never, string command)
	{
		StringBuilder state = new StringBuilder();
		if (context.Length > 0)
			state.Append(context).Append("\n\n");
		AppendList(state, "Commands this person has said may ALWAYS run:", always);
		AppendList(state, "Commands this person has said must NEVER run:",  never);
		state.Append(Framing).Append('\n').Append(command);
		return state.ToString();
	}

	private static void AppendList(StringBuilder state, string heading, List<string> commands)
	{
		if (commands.Count > 0)
		{
			state.Append(heading).Append('\n');
			foreach (string command in commands)
				state.Append("- ").Append(command.Replace("\n", "\n  ")).Append('\n');
			state.Append('\n');
		}
	}

	// One model's answers reduced to the two scores the verdict is drawn from. AllowScore is the highest
	// chance of any "yes" or of "block": Allow needs all of them low. BlockScore averages, in log-odds,
	// the final question's chance of "block" with the strongest "yes": both must lean dangerous to block.
	// Concerns names the yes/no questions the model answered "yes". Error is set when an answer is
	// missing or picks an option that was never offered.
	public static (double AllowScore, double BlockScore, List<string> Concerns, string Error) Score(JsonObject answers)
	{
		double       allowScore = 0;
		double       strongest  = 0;
		double       final      = 0.5;
		List<string> concerns   = new List<string>();
		string       error      = string.Empty;

		foreach ((string key, DecisionQuestion question) in Questions)
		{
			JsonObject? answer = answers[key] as JsonObject;
			string      choice = answer?["choice"] is JsonValue c && c.TryGetValue(out string? s) ? s : string.Empty;
			if (answer == null || !question.Options.TryGetValue(choice, out DecisionOption? picked))
			{
				error = answer == null ? $"no answer for '{key}'" : $"'{key}' chose '{choice}', which was not offered";
				break;
			}

			double p   = Math.Clamp(FlagProbability(question, answer, picked), 1e-6, 1 - 1e-6);
			allowScore = Math.Max(allowScore, p);
			if (key == FinalQuestion)
			{
				final = p;
			}
			else
			{
				strongest = Math.Max(strongest, p);
				if (picked.Flag)
					concerns.Add(key);
			}
		}

		double blockScore = 1 / (1 + Math.Exp(-(Logit(final) + Logit(strongest)) / 2));
		return (allowScore, blockScore, concerns, error);
	}

	// The verdict from the two scores and one model's calibrated boundaries. The returned score is the
	// one the verdict was decided on.
	public static (HallMonitorJudgment Judgment, double Score) Classify(double allowScore, double blockScore, double allowAtMost, double blockAtLeast)
	{
		HallMonitorJudgment judgment = HallMonitorJudgment.Unsure;
		double              score    = allowScore;
		if (blockScore >= blockAtLeast)
		{
			judgment = HallMonitorJudgment.Block;
			score    = blockScore;
		}
		else if (allowScore <= allowAtMost)
		{
			judgment = HallMonitorJudgment.Allow;
		}
		return (judgment, score);
	}

	// Why a command was not allowed: the concerns raised, by question, and the first one's meaning.
	private static string ReasonFor(HallMonitorJudgment judgment, double score, List<string> concerns)
	{
		string reason = string.Empty;
		if (judgment != HallMonitorJudgment.Allow)
			reason = concerns.Count > 0
				? $"{string.Join(", ", concerns)}: {Questions[concerns[0]].Options["yes"].Description}"
				: $"not confident it is safe (score {score:0.00})";
		return reason;
	}

	private static double Logit(double p)
	{
		double clamped = Math.Clamp(p, 1e-6, 1 - 1e-6);
		return Math.Log(clamped / (1 - clamped));
	}

	// A question's chance of flagging: the probability on its flag options (the others take the rest),
	// or the model's confidence in the option it chose when it gives no probabilities.
	private static double FlagProbability(DecisionQuestion question, JsonObject answer, DecisionOption picked)
	{
		double p;
		if (answer["probabilities"] is JsonObject probabilities)
		{
			double flagged = 0;
			double total   = 0;
			foreach ((string option, DecisionOption meaning) in question.Options)
			{
				double value = probabilities[option] is JsonValue v && v.TryGetValue(out double d) ? d : 0;
				total       += value;
				if (meaning.Flag)
					flagged += value;
			}
			p = total > 0 ? flagged / total : 0.5;
		}
		else if (answer["confidence"] is JsonValue cv && cv.TryGetValue(out double confidence))
		{
			p = picked.Flag ? confidence : 1 - confidence;
		}
		else
		{
			p = picked.Flag ? 1 : 0;
		}
		return p;
	}
}