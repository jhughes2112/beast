using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// Output modalities and the decisions role: catalogs declare what a model produces, roles state what
// they need, and the two only ever meet when they match. HallMonitor scores a decision model's yes/no
// answers into Allow, Unsure or Block with boundaries calibrated per model, automatically, the first
// time the model is used.
public static class DecisionRoleTests
{
	private const string kChatId   = "chat-model";
	private const string kDecideId = "decide-model";

	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  DecisionRoleTests");

		TestCatalogOutputs(ctx);
		TestDecisionEndpoint(ctx);
		TestProduces(ctx);
		TestWildcardsFollowOutput(ctx);
		TestEnablementFollowsOutput(ctx);
		TestSelectionFollowsOutput(ctx);
		TestHallMonitorRole(ctx);
		TestRequestShape(ctx);
		TestScoring(ctx);
		TestActions(ctx);
		TestCalibrationRecommendation(ctx);
		TestCalibrationCommandsAndStore(ctx);
		await TestNoModelIsUnsureAsync(ctx);
		await TestAutomaticCalibrationAsync(ctx);
		await TestMemoryAsync(ctx);
	}

	private static void TestCatalogOutputs(TestContext ctx)
	{
		JsonNode decider = JsonNode.Parse("""
			{"id":"llama.cpp/clef-flash-q4km","context_length":16384,
			 "architecture":{"input_modalities":["text"],"output_modalities":["decisions"]}}
			""")!;
		DiscoveredModel d = (DiscoveredModel)Reflect.Static(typeof(ModelCatalog), "ParseEntry", new[] { typeof(JsonNode), typeof(bool) }, new object[] { decider, false })!;
		ctx.AssertEqual("decisions", d.Outputs?[0] ?? string.Empty, "Catalog: output_modalities read");

		JsonNode plain = JsonNode.Parse("""{"id":"m","architecture":{"input_modalities":["text"]}}""")!;
		DiscoveredModel p = (DiscoveredModel)Reflect.Static(typeof(ModelCatalog), "ParseEntry", new[] { typeof(JsonNode), typeof(bool) }, new object[] { plain, false })!;
		ctx.Assert(p.Outputs == null, "Catalog: undeclared outputs stay null");

		List<DiscoveredModel> xai = new List<DiscoveredModel> { new DiscoveredModel { Id = "grok-x" } };
		ModelCatalog.ParseXaiList(JsonNode.Parse("""[{"id":"grok-x","input_modalities":["text"],"output_modalities":["text"]}]""")!.AsArray(), xai);
		ctx.AssertEqual("text", xai[0].Outputs?[0] ?? string.Empty, "Catalog: xAI output_modalities read");
	}

	private static void TestDecisionEndpoint(TestContext ctx)
	{
		ctx.AssertEqual("http://peachy.local/v1/systemone", ModelCatalog.DecisionEndpoint("http://peachy.local/v1"), "DecisionEndpoint: from an api root");
		ctx.AssertEqual("http://peachy.local/v1/systemone", ModelCatalog.DecisionEndpoint("http://peachy.local"), "DecisionEndpoint: from a bare host");
		ctx.AssertEqual("http://peachy.local/v1/systemone", ModelCatalog.DecisionEndpoint("http://peachy.local/v1/chat/completions"), "DecisionEndpoint: from a chat route");
		ctx.AssertEqual("http://peachy.local/v1/systemone", ModelCatalog.NormalizeRequestEndpoint("http://peachy.local/v1/systemone"), "NormalizeRequestEndpoint: keeps /systemone");
	}

	private static void TestProduces(TestContext ctx)
	{
		List<string> none    = new List<string>();
		List<string> decides = new List<string> { "decisions" };
		ctx.Assert(OutputModality.Produces(none, OutputModality.Text), "Produces: undeclared means text");
		ctx.Assert(!OutputModality.Produces(none, OutputModality.Decisions), "Produces: undeclared is not decisions");
		ctx.Assert(!OutputModality.Produces(decides, OutputModality.Text), "Produces: a decision model is not text");
		ctx.Assert(OutputModality.DecisionsOnly(decides), "DecisionsOnly: decisions alone");
		ctx.Assert(!OutputModality.DecisionsOnly(new List<string> { "text", "decisions" }), "DecisionsOnly: not when it also chats");
	}

	private static Role TextRole(List<string> models)
	{
		return new Role("TextTestRole", "t", RoleKind.Subagent, models, new List<string>(), "s", string.Empty, string.Empty);
	}

	private static Role DecideRole(List<string> models)
	{
		return new Role("DecideTestRole", "d", RoleKind.Subagent, models, new List<string>(), string.Empty, string.Empty, string.Empty,
			0, new Dictionary<string, int>(), false, OutputModality.Decisions);
	}

	private static void TestWildcardsFollowOutput(TestContext ctx)
	{
		RoleService roles            = new RoleService(Environment.CurrentDirectory);
		roles.Roles["TextTestRole"] = TextRole(new List<string> { "*" });
		roles.Roles["DecideTestRole"] = DecideRole(new List<string> { "*" });

		roles.ExpandModelWildcards(new List<(string Id, List<string> Outputs)>
		{
			(kChatId, new List<string> { "text" }),
			(kDecideId, new List<string> { "decisions" })
		});

		ctx.AssertEqual(kChatId, string.Join(",", roles.Roles["TextTestRole"].Models), "Wildcard: a text role takes only the chat model");
		ctx.AssertEqual(kDecideId, string.Join(",", roles.Roles["DecideTestRole"].Models), "Wildcard: a decisions role takes only the decision model");
		ctx.AssertEqual(OutputModality.Decisions, roles.Roles["DecideTestRole"].OutputModality, "Wildcard: expansion keeps the output modality");
	}

	private static void TestEnablementFollowsOutput(TestContext ctx)
	{
		List<(string Id, List<string> Outputs)> added = new List<(string Id, List<string> Outputs)>
		{
			(kChatId, new List<string> { "text" }),
			(kDecideId, new List<string> { "decisions" })
		};
		Type[] signature = { typeof(Role), typeof(List<(string Id, List<string> Outputs)>), typeof(List<string>) };

		List<string>? text = (List<string>?)Reflect.Static(typeof(RoleService), "ApplyEnablementToList", signature, new object[] { TextRole(new List<string> { "*" }), added, new List<string>() });
		List<string>? dec  = (List<string>?)Reflect.Static(typeof(RoleService), "ApplyEnablementToList", signature, new object[] { DecideRole(new List<string> { "*" }), added, new List<string>() });

		ctx.AssertEqual($"{kChatId},*", string.Join(",", text ?? new List<string>()), "Enablement: a new chat model joins the text role only");
		ctx.AssertEqual($"{kDecideId},*", string.Join(",", dec ?? new List<string>()), "Enablement: a new decision model joins the decisions role only");
	}

	private static LlmRegistry RegistryWith(params LlmModel[] models)
	{
		Dictionary<string, LlmModel> byId = new Dictionary<string, LlmModel>(StringComparer.OrdinalIgnoreCase);
		foreach (LlmModel model in models)
			byId[model.ConfigId] = model;
		LlmRegistry registry = new LlmRegistry();
		registry.RestoreModels(byId);
		return registry;
	}

	private static LlmModel Model(string id, string endpoint, string output)
	{
		ModelConfig config = new ModelConfig { Id = id, Name = id, ContextWindow = 16384, Output = new List<string> { output } };
		return new LlmModel(id, endpoint, string.Empty, new List<JsonObject>(), new List<JsonObject>(), config);
	}

	// Explicitly listed models of the wrong kind are passed over, wherever they sit in the list.
	private static void TestSelectionFollowsOutput(TestContext ctx)
	{
		LlmRegistry registry = RegistryWith(Model(kChatId, "http://127.0.0.1:9/v1/chat/completions", "text"), Model(kDecideId, "http://127.0.0.1:9/v1/systemone", "decisions"));
		Role        text     = TextRole(new List<string> { kDecideId, kChatId });
		Role        decide   = DecideRole(new List<string> { kChatId, kDecideId });

		ctx.AssertEqual(kChatId, registry.GetModelForRole(text, string.Empty, 0)?.ConfigId ?? string.Empty, "Select: a text role skips a listed decision model");
		ctx.AssertEqual(kDecideId, registry.GetModelForRole(decide, string.Empty, 0)?.ConfigId ?? string.Empty, "Select: a decisions role skips a listed chat model");
		ctx.Assert(registry.GetModelForRole(text, kDecideId, 0)?.ConfigId == kChatId, "Select: a preferred decision model is refused for a text role");
		ctx.AssertEqual(kChatId, string.Join(",", registry.GetEnabledModelsForRole(text)), "Select: enabled models filtered by output");

		List<LlmModel> forDecide = registry.ModelsForRole(decide);
		ctx.Assert(forDecide.Count == 1 && forDecide[0].ConfigId == kDecideId, "ModelsForRole: decision models only");

		LlmService? service = registry.CreateService(text, string.Empty, 0, false);
		ctx.AssertEqual(kChatId, string.Join(",", service?.RoleModelIds ?? new List<string>()), "CreateService: fallback list holds only models that serve the role");
	}

	// The role holds only the model order; the questions are in code. roles.json carries no question text.
	private static void TestHallMonitorRole(TestContext ctx)
	{
		RoleService roles = new RoleService(Environment.CurrentDirectory);
		Role?       hall  = roles.GetRole(HallMonitor.RoleName);
		ctx.Assert(hall != null, "HallMonitor: role is defined");
		ctx.AssertEqual(OutputModality.Decisions, hall?.Produces ?? string.Empty, "HallMonitor: produces decisions");
		ctx.AssertEqual(10, HallMonitor.Questions.Count, "HallMonitor: nine yes/no questions and the final one, in code");
		ctx.Assert(HallMonitor.Questions.ContainsKey(HallMonitor.FinalQuestion), "HallMonitor: the final allow/block question is present");

		RoleService.RolesFile file = new RoleService.RolesFile { Version = 12 };
		file.Subagents.Add(DecideRole(new List<string> { "*" }));
		string json = JsonSerializer.Serialize(file, BeastJson.Persist.RolesFile);
		Role   back = JsonSerializer.Deserialize(json, BeastJson.Config.RolesFile)!.Subagents[0];
		ctx.AssertEqual(OutputModality.Decisions, back.OutputModality, "Roles: output_modality round-trips");
		ctx.Assert(!json.Contains("questions") && !json.Contains("allow_at_most"), "Roles: no question or boundary is written to roles.json");

		RoleService.RolesFile legacy = JsonSerializer.Deserialize("""{"version":11,"agents":[],"subagents":[{"name":"Old","models":["*"],"questions":{"x":{}},"allow_at_most":0.3}]}""", BeastJson.Config.RolesFile)!;
		ctx.AssertEqual(OutputModality.Text, legacy.Subagents[0].Produces, "Roles: an older file with decision fields still loads");
	}

	private static void TestRequestShape(TestContext ctx)
	{
		JsonObject body = DecisionClient.BuildRequest("m", HallMonitor.BuildState(string.Empty, new List<string>(), new List<string>(), "ls"), HallMonitor.Questions);
		ctx.AssertEqual("m", body["model"]!.GetValue<string>(), "Request: model");
		ctx.AssertEqual($"{HallMonitor.Framing}\nls", body["state"]!.GetValue<string>(), "Request: state");
		ctx.AssertEqual("choice", body["questions"]!["root"]!["type"]!.GetValue<string>(), "Request: asked as a choice");
		ctx.Assert(body["questions"]!["root"]!["criteria"]!["yes"]!.GetValue<string>().StartsWith("Yes:"), "Request: options become criteria");
	}

	// Answers for every HallMonitor question: each yes/no question's chance of "yes" (default 0.02 when
	// not given), and the final question's chance of "dangerous".
	private static JsonObject Answers(Dictionary<string, double> yes, double dangerous)
	{
		JsonObject answers = new JsonObject();
		foreach ((string key, DecisionQuestion _) in HallMonitor.Questions)
		{
			if (key == HallMonitor.FinalQuestion)
			{
				answers[key] = new JsonObject { ["choice"] = dangerous >= 0.5 ? "dangerous" : "safe", ["probabilities"] = new JsonObject { ["safe"] = 1 - dangerous, ["dangerous"] = dangerous } };
			}
			else
			{
				double p = yes.TryGetValue(key, out double given) ? given : 0.02;
				answers[key] = new JsonObject { ["choice"] = p >= 0.5 ? "yes" : "no", ["probabilities"] = new JsonObject { ["yes"] = p, ["no"] = 1 - p } };
			}
		}
		return answers;
	}

	private static HallMonitorJudgment Judge(Dictionary<string, double> yes, double dangerous, double allowAtMost, double blockAtLeast)
	{
		(double allowScore, double blockScore, List<string> _, string _) = HallMonitor.Score(Answers(yes, dangerous));
		return HallMonitor.Classify(allowScore, blockScore, allowAtMost, blockAtLeast).Judgment;
	}

	// Allow needs every answer comfortable; Block needs the final judgment and the strongest concern together.
	private static void TestScoring(TestContext ctx)
	{
		Dictionary<string, double> none = new Dictionary<string, double>();
		ctx.Assert(Judge(none, 0.05, 0.30, 0.80) == HallMonitorJudgment.Allow, "Score: every answer comfortable is Allow");
		ctx.Assert(Judge(new Dictionary<string, double> { ["root"] = 0.6 }, 0.05, 0.30, 0.80) == HallMonitorJudgment.Unsure, "Score: one concern keeps a command out of Allow");
		ctx.Assert(Judge(new Dictionary<string, double> { ["unchecked_variable"] = 0.95 }, 0.10, 0.30, 0.80) == HallMonitorJudgment.Unsure, "Score: one strong concern alone cannot Block");
		ctx.Assert(Judge(none, 0.95, 0.30, 0.80) == HallMonitorJudgment.Unsure, "Score: the final judgment alone cannot Block");
		ctx.Assert(Judge(new Dictionary<string, double> { ["loses_data"] = 0.9 }, 0.9, 0.30, 0.80) == HallMonitorJudgment.Block, "Score: a concern and the final judgment together Block");

		(double _, double _, List<string> concerns, string error) = HallMonitor.Score(Answers(new Dictionary<string, double> { ["root"] = 0.9, ["loses_data"] = 0.7 }, 0.9));
		ctx.Assert(error.Length == 0 && concerns.Contains("root") && concerns.Contains("loses_data") && concerns.Count == 2, "Score: concerns name the questions answered yes");

		JsonObject missing = Answers(none, 0.05);
		missing.Remove("root");
		ctx.Assert(HallMonitor.Score(missing).Error.Contains("root"), "Score: a missing answer is an error");
	}

	// Allow and Block never depend on the policy; Unsure always does.
	private static void TestActions(TestContext ctx)
	{
		HallMonitorVerdict allow  = new HallMonitorVerdict(HallMonitorJudgment.Allow, 0.05, string.Empty, "m");
		HallMonitorVerdict block  = new HallMonitorVerdict(HallMonitorJudgment.Block, 0.95, "r", "m");
		HallMonitorVerdict unsure = new HallMonitorVerdict(HallMonitorJudgment.Unsure, 0.4, "r", "m");
		foreach (UnsurePolicy policy in new[] { UnsurePolicy.Ask, UnsurePolicy.BeSafe, UnsurePolicy.BeUnsafe })
		{
			ctx.Assert(allow.ActionFor(policy) == HallMonitorAction.Run, $"Action: Allow runs under {policy}");
			ctx.Assert(block.ActionFor(policy) == HallMonitorAction.Refuse, $"Action: Block refuses under {policy}");
		}
		ctx.Assert(unsure.ActionFor(UnsurePolicy.Ask) == HallMonitorAction.AskHuman, "Action: Unsure asks under Ask");
		ctx.Assert(unsure.ActionFor(UnsurePolicy.BeSafe) == HallMonitorAction.Refuse, "Action: Unsure refuses under BeSafe");
		ctx.Assert(unsure.ActionFor(UnsurePolicy.BeUnsafe) == HallMonitorAction.Run, "Action: Unsure runs under BeUnsafe");
	}

	// Allow goes 0.15 under the lowest dangerous allow score; Block just above the highest safe block score,
	// never under 0.70, and never at all when that would pass 0.95.
	private static void TestCalibrationRecommendation(TestContext ctx)
	{
		(double allow, double block, string problem) = HallMonitorCalibration.Recommend(new List<(double AllowScore, double BlockScore, bool Dangerous)>
		{
			(0.05, 0.04, false), (0.30, 0.52, false), (0.62, 0.40, true), (0.97, 0.93, true)
		});
		ctx.Assert(problem.Length == 0, "Calibrate: separable scores are usable");
		ctx.AssertEqual(0.45, allow, "Calibrate: Allow sits 0.15 under the lowest dangerous score");
		ctx.AssertEqual(0.70, block, "Calibrate: Block stays at its floor when every safe score is below it");

		(double _, double noBlock, string _) = HallMonitorCalibration.Recommend(new List<(double AllowScore, double BlockScore, bool Dangerous)>
		{
			(0.05, 0.96, false), (0.80, 0.97, true)
		});
		ctx.AssertEqual(1.0, noBlock, "Calibrate: a safe command scoring near certain means the model never blocks");

		(double _, double _, string bad) = HallMonitorCalibration.Recommend(new List<(double AllowScore, double BlockScore, bool Dangerous)>
		{
			(0.05, 0.04, false), (0.12, 0.10, true)
		});
		ctx.Assert(bad.Length > 0, "Calibrate: a dangerous command scored as safe makes the model unusable");
	}

	private static void TestCalibrationCommandsAndStore(TestContext ctx)
	{
		int dangerous = 0;
		foreach ((string _, bool isDangerous) in HallMonitorCalibration.Commands)
			dangerous += isDangerous ? 1 : 0;
		ctx.AssertEqual(594, HallMonitorCalibration.Commands.Count, "Calibration set: 594 commands are built in");
		ctx.AssertEqual(234, dangerous, "Calibration set: 234 of them dangerous");
		ctx.AssertEqual(16, HallMonitorCalibration.Fingerprint.Length, "Calibration set: fingerprinted");

		string path = Path.Combine(Path.GetTempPath(), $"beast-calibration-{Guid.NewGuid():N}.json");
		try
		{
			File.WriteAllText(path, $$$"""{"m":{"fingerprint":"{{{HallMonitorCalibration.Fingerprint}}}","usable":true,"allow_at_most":0.3,"block_at_least":0.85},"old":{"fingerprint":"0000000000000000","usable":true,"allow_at_most":0.3,"block_at_least":0.85}}""");
			HallMonitorCalibration store = new HallMonitorCalibration(path, new DecisionMemory(TempPath("decisions")), _ => { }, CancellationToken.None);
			CalibrationResult?     m     = store.ResultFor(Model("m", "http://127.0.0.1:9/v1/systemone", "decisions"));
			ctx.Assert(m != null && m.Usable && m.AllowAtMost == 0.3 && m.BlockAtLeast == 0.85, "Calibration store: a current result is read");
			ctx.Assert(store.ResultFor(Model("old", "http://127.0.0.1:9/v1/systemone", "decisions")) == null, "Calibration store: a result for other questions or commands is stale");
		}
		finally
		{
			File.Delete(path);
		}
	}

	// No decision model is enabled for HallMonitor here (the registry is empty), so nothing can judge the
	// command: it is Unsure and goes wherever the Unsure policy sends it.
	private static async Task TestNoModelIsUnsureAsync(TestContext ctx)
	{
		string memoryPath      = TempPath("decisions");
		string calibrationPath = TempPath("calibration");
		try
		{
			DecisionMemory     memory  = new DecisionMemory(memoryPath);
			HallMonitor        monitor = new HallMonitor(new RoleService(Environment.CurrentDirectory), new LlmRegistry(), memory, new HallMonitorCalibration(calibrationPath, memory, _ => { }, CancellationToken.None));
			HallMonitorVerdict verdict = await monitor.CheckAsync("ls -la", CancellationToken.None);
			ctx.Assert(verdict.Judgment == HallMonitorJudgment.Unsure, "HallMonitor: no decision model means Unsure");
			ctx.Assert(verdict.ActionFor(UnsurePolicy.Ask) == HallMonitorAction.AskHuman, "HallMonitor: no decision model follows the Unsure policy");
			ctx.Assert(verdict.Reason.Contains("no decision model"), "HallMonitor: says why it could not judge");
		}
		finally
		{
			File.Delete(memoryPath);
			File.Delete(calibrationPath);
		}
	}

	// A fake decision model that answers each calibration command by its label (a concern and "dangerous"
	// for a dangerous one, everything comfortable for a safe one), and any other command by a few obvious
	// patterns. The first check finds the model uncalibrated, so it is Unsure and calibration starts; once
	// the 594 commands are through, the stored boundaries Allow and Block on the model's answers.
	private static async Task TestAutomaticCalibrationAsync(TestContext ctx)
	{
		Dictionary<string, bool> labels = new Dictionary<string, bool>(StringComparer.Ordinal);
		foreach ((string command, bool dangerous) in HallMonitorCalibration.Commands)
			labels[command] = dangerous;

		HttpListener? listener = null;
		int           port     = 0;
		for (int attempt = 0; attempt < 10 && listener == null; attempt++)
		{
			HttpListener candidate = new HttpListener();
			port = 40000 + Random.Shared.Next(20000);
			candidate.Prefixes.Add($"http://localhost:{port}/");
			try
			{
				candidate.Start();
				listener = candidate;
			}
			catch (HttpListenerException)
			{
			}
		}
		if (listener == null)
			ctx.Log("  SKIP: automatic calibration (no local port for the fake decision model)");
		else
			await RunAutomaticCalibrationAsync(ctx, listener, port, labels);
	}

	private static async Task RunAutomaticCalibrationAsync(TestContext ctx, HttpListener listener, int port, Dictionary<string, bool> labels)
	{
		using CancellationTokenSource cts     = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		Task                          serving = ServeFakeDeciderAsync(listener, labels, cts.Token);

		string                  memoryPath      = TempPath("decisions");
		string                  calibrationPath = TempPath("calibration");
		List<CalibrationStatus> statuses        = new List<CalibrationStatus>();
		try
		{
			LlmModel    decider = Model(kDecideId, $"http://localhost:{port}/v1/systemone", "decisions");
			RoleService roles   = new RoleService(Environment.CurrentDirectory);
			roles.Roles[HallMonitor.RoleName] = roles.GetRole(HallMonitor.RoleName)!.WithModels(new List<string> { kDecideId });

			// One answer of each kind, new to the built-in set, plus one repeating a built-in command: the new
			// ones join the calibration set with their labels, the repeat keeps the built-in label and is not added twice.
			// Not the commands checked below, which an Always answer would decide without the model.
			string builtIn = string.Empty;
			foreach ((string command, bool _) in HallMonitorCalibration.Commands)
			{
				if (builtIn.Length == 0 && command != "ls -la" && command != "rm -rf ~/")
					builtIn = command;
			}
			File.WriteAllText(memoryPath, new JsonObject { ["always"] = new JsonArray("npm run beast-lint-check", builtIn), ["never"] = new JsonArray("sudo rm -rf /srv/data") }.ToJsonString());

			DecisionMemory         memory      = new DecisionMemory(memoryPath);
			HallMonitorCalibration calibration = new HallMonitorCalibration(calibrationPath, memory, s => { lock (statuses) statuses.Add(s); }, cts.Token);
			HallMonitor            monitor     = new HallMonitor(roles, RegistryWith(decider), memory, calibration);

			List<(string Command, bool Dangerous)> set = calibration.CalibrationSet();
			ctx.AssertEqual(596, set.Count, "Calibration set: the person's new answers join the built-in commands, a repeat does not");
			ctx.Assert(set.Contains(("npm run beast-lint-check", false)) && set.Contains(("sudo rm -rf /srv/data", true)), "Calibration set: Always is labelled safe and Never dangerous");

			HallMonitorVerdict first = await monitor.CheckAsync("ls -la", cts.Token);
			ctx.Assert(first.Judgment == HallMonitorJudgment.Unsure && first.Reason.Contains("calibrat"), $"Auto-calibration: an uncalibrated model is not asked, and the command is Unsure (got {first.Judgment}: {first.Reason})");

			bool finished = false;
			while (!finished && !cts.IsCancellationRequested)
			{
				await Task.Delay(100, cts.Token);
				lock (statuses)
					finished = statuses.Count > 0 && statuses[statuses.Count - 1].State != "running";
			}

			CalibrationResult? result = new HallMonitorCalibration(calibrationPath, memory, _ => { }, cts.Token).ResultFor(decider);
			ctx.Assert(result != null && result.Usable && result.Failed == 0, "Auto-calibration: the result is saved and usable");
			ctx.AssertEqual(596, result?.Commands ?? 0, "Auto-calibration: measured over the built-in commands and the person's answers");
			ctx.AssertEqual(0.75, result?.AllowAtMost ?? 0, "Auto-calibration: Allow sits 0.15 under the lowest dangerous score");
			ctx.AssertEqual(0.70, result?.BlockAtLeast ?? 0, "Auto-calibration: Block at its floor");
			lock (statuses)
			{
				ctx.Assert(statuses[0].State == "running" && statuses[0].Total == 596, "Auto-calibration: progress starts with every decision to go");
				ctx.Assert(statuses.Count > 10 && statuses[statuses.Count - 1].State == "done" && statuses[statuses.Count - 1].Remaining == 0, "Auto-calibration: progress counts down to done");
			}

			// A new answer after calibration does not make the result stale: no recalibration until the next one.
			monitor.RecordHumanAnswer("make test", HumanAnswer.Always);
			ctx.Assert(calibration.ResultFor(decider) != null, "Auto-calibration: a new answer does not trigger a recalibration");

			ctx.Assert((await monitor.CheckAsync("ls -la", cts.Token)).Judgment == HallMonitorJudgment.Allow, "Auto-calibration: a safe command is then Allow");
			HallMonitorVerdict wipe = await monitor.CheckAsync("rm -rf ~/", cts.Token);
			ctx.Assert(wipe.Judgment == HallMonitorJudgment.Block && wipe.ModelId == kDecideId, "Auto-calibration: a dangerous command is then Block");
		}
		finally
		{
			cts.Cancel();
			listener.Stop();
			try
			{
				await serving;
			}
			catch (Exception)
			{
			}
			File.Delete(memoryPath);
			File.Delete(calibrationPath);
		}
	}

	private static async Task ServeFakeDeciderAsync(HttpListener listener, Dictionary<string, bool> labels, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			HttpListenerContext context = await listener.GetContextAsync();
			string              body;
			using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
				body = await reader.ReadToEndAsync(ct);

			string state     = JsonNode.Parse(body)?["state"]?.GetValue<string>() ?? string.Empty;
			string command   = state.Substring(state.LastIndexOf('\n') + 1);
			bool   dangerous = labels.TryGetValue(command, out bool label) ? label : command.Contains("rm -rf ~") || command.StartsWith("sudo");

			JsonObject reply = new JsonObject { ["answers"] = Answers(dangerous ? new Dictionary<string, double> { ["loses_data"] = 0.9 } : new Dictionary<string, double>(), dangerous ? 0.9 : 0.05) };
			byte[]     bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
			context.Response.ContentType = "application/json";
			await context.Response.OutputStream.WriteAsync(bytes, ct);
			context.Response.Close();
		}
	}

	// Always and Never answers are kept in decisions.json and decide exact repeats without a model.
	// BuildState can still lead a state with the lists (the live learning test measures that), but
	// HallMonitor's own decisions never pass them.
	private static async Task TestMemoryAsync(TestContext ctx)
	{
		string path            = TempPath("decisions");
		string calibrationPath = TempPath("calibration");
		try
		{
			DecisionMemory memory  = new DecisionMemory(path);
			HallMonitor    monitor = new HallMonitor(new RoleService(Environment.CurrentDirectory), new LlmRegistry(), memory, new HallMonitorCalibration(calibrationPath, memory, _ => { }, CancellationToken.None));

			ctx.AssertEqual("always,yes,no,never", string.Join(",", Array.ConvertAll(HallMonitor.HumanChoices, a => a.ToString().ToLowerInvariant())), "Ask: the four choices, in order");
			ctx.AssertEqual("Run this command? make clean", HallMonitor.AskText("make clean"), "Ask: the question");

			ctx.Assert(monitor.RecordHumanAnswer("make deploy", HumanAnswer.Yes), "Answer: yes runs");
			ctx.Assert(!monitor.RecordHumanAnswer("rm -rf data", HumanAnswer.No), "Answer: no does not run");
			(List<string> Always, List<string> Never) afterOnce = memory.Snapshot();
			ctx.Assert(afterOnce.Always.Count == 0 && afterOnce.Never.Count == 0, "Answer: yes and no are not remembered");

			ctx.Assert(monitor.RecordHumanAnswer("  npm run lint  ", HumanAnswer.Always), "Answer: always runs");
			ctx.Assert(!monitor.RecordHumanAnswer("docker system prune -af", HumanAnswer.Never), "Answer: never does not run");
			monitor.RecordHumanAnswer("npm run lint", HumanAnswer.Always);

			DecisionMemory                            reread = new DecisionMemory(path);
			(List<string> Always, List<string> Never) saved  = reread.Snapshot();
			ctx.AssertEqual("npm run lint", string.Join("|", saved.Always), "Memory: always is saved trimmed and once");
			ctx.AssertEqual("docker system prune -af", string.Join("|", saved.Never), "Memory: never is saved");

			ctx.Assert((await monitor.CheckAsync("npm run lint", CancellationToken.None)).Judgment == HallMonitorJudgment.Allow, "Memory: an Always command is Allow without a model");
			ctx.Assert((await monitor.CheckAsync("docker system prune -af ", CancellationToken.None)).Judgment == HallMonitorJudgment.Block, "Memory: a Never command is Block without a model");
			ctx.Assert((await monitor.CheckAsync("npm run lint --fix", CancellationToken.None)).Judgment == HallMonitorJudgment.Unsure, "Memory: only exact repeats are decided from memory");

			// Another session (or a hand edit) adds an entry; this session's next answer must keep it.
			File.WriteAllText(path, """{"always":["npm run lint","make test"],"never":["docker system prune -af"]}""");
			File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
			monitor.RecordHumanAnswer("git clean -fdx", HumanAnswer.Never);
			(List<string> Always, List<string> Never) merged = new DecisionMemory(path).Snapshot();
			ctx.Assert(merged.Always.Contains("make test") && merged.Never.Contains("git clean -fdx"), "Memory: saving keeps what another session added");

			File.WriteAllText(path, """{"always":["git status"],"never":[]}""");
			File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
			ctx.AssertEqual("git status", string.Join("|", memory.Snapshot().Always), "Memory: a hand edit is picked up");

			string first  = HallMonitor.BuildState(string.Empty, new List<string> { "a" }, new List<string> { "b" }, "ls");
			string second = HallMonitor.BuildState(string.Empty, new List<string> { "a" }, new List<string> { "b" }, "pwd");
			string prefix = $"Commands this person has said may ALWAYS run:\n- a\n\nCommands this person has said must NEVER run:\n- b\n\n{HallMonitor.Framing}\n";
			ctx.Assert(first == prefix + "ls" && second == prefix + "pwd", "State: lists first, then the framing, then the command");
			ctx.AssertEqual($"{HallMonitor.Framing}\nls", HallMonitor.BuildState(string.Empty, new List<string>(), new List<string>(), "ls"), "State: no lists, just the framing and command");
			ctx.AssertEqual($"Context.\n\n{HallMonitor.Framing}\nls", HallMonitor.BuildState("Context.", new List<string>(), new List<string>(), "ls"), "State: the context comes first of all");
		}
		finally
		{
			File.Delete(path);
			File.Delete(calibrationPath);
		}
	}

	private static string TempPath(string kind)
	{
		return Path.Combine(Path.GetTempPath(), $"beast-{kind}-{Guid.NewGuid():N}.json");
	}
}