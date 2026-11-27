using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;


// Per-model request settings (extras, headers, timeout) and the per-role budget/reasoning fields
// survive the settings and roles files, and a no_reasoning role runs its models quiet.
public static class ModelSettingsTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  ModelSettingsTests");

		TestAutoModelRoundTrip(ctx);
		TestSparseWhenUnset(ctx);
		TestRoleFieldsRoundTrip(ctx);
		TestWithoutReasoningKeepsTimeout(ctx);
		TestSessionRoleOrder(ctx);
	}

	// /role sets a role's model order for its own session only: kept in that session's state folder, so a
	// restart keeps it and another session is untouched. It is order alone — the role's models still come
	// from roles.json, so one added there joins and one removed there leaves.
	private static void TestSessionRoleOrder(TestContext ctx)
	{
		Type[]       types  = new Type[] { typeof(List<string>), typeof(List<string>) };
		List<string> joined = (List<string>)Reflect.Static(typeof(RoleService), "Prioritized", types, new object[] { new List<string> { "a", "b", "c", "d" }, new List<string> { "c", "gone", "a" } })!;
		ctx.AssertEqual("c,a,b,d", string.Join(",", joined), "SessionOrder: the session's models first in its order, the rest in the global order, missing ones passed over");
		List<string> starred = (List<string>)Reflect.Static(typeof(RoleService), "Prioritized", types, new object[] { new List<string> { "a", "*" }, new List<string> { "*", "a" } })!;
		ctx.AssertEqual("a,*", string.Join(",", starred), "SessionOrder: a wildcard is never put first");

		string previous = AgentState.Root;
		string mine     = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"beast-roleorder-{Guid.NewGuid():N}");
		string other    = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"beast-roleorder-{Guid.NewGuid():N}");
		List<(string Id, List<string> Outputs)> fakes = new List<(string Id, List<string> Outputs)> { ("order-a", new List<string> { "text" }), ("order-b", new List<string> { "text" }), ("order-c", new List<string> { "text" }) };
		try
		{
			AgentState.Initialize(mine);
			RoleService first = new RoleService(Environment.CurrentDirectory);
			first.ExpandModelWildcards(fakes);
			List<string> global = new List<string>(first.Roles["Developer"].Models);
			if (global.Count < 2)
			{
				ctx.Log("    SKIP session role order: the Developer role has fewer than two models here");
				return;
			}

			List<string> reversed = new List<string>(global);
			reversed.Reverse();
			ctx.Assert(                       first.SaveRoleModelOrder("Developer", reversed), "SessionOrder: a /role order is saved");
			ctx.Assert(System.IO.File.Exists(System.IO.Path.Combine(mine, "role-order.json")), "SessionOrder: in the session's own state folder");

			RoleService restarted = new RoleService(Environment.CurrentDirectory);
			restarted.ExpandModelWildcards(fakes);
			ctx.AssertEqual(string.Join(",", reversed), string.Join(",", restarted.Roles["Developer"].Models), "SessionOrder: a restart keeps it");

			AgentState.Initialize(other);
			RoleService elsewhere = new RoleService(Environment.CurrentDirectory);
			elsewhere.ExpandModelWildcards(fakes);
			ctx.AssertEqual(string.Join(",", global), string.Join(",", elsewhere.Roles["Developer"].Models), "SessionOrder: another session keeps the global order");
		}
		finally
		{
			AgentState.Initialize(previous == System.IO.Path.Combine(Environment.CurrentDirectory, ".beast") ? string.Empty : previous);
			foreach (string dir in new string[] { mine, other })
			{
				if (System.IO.Directory.Exists(dir))
				{
					System.IO.Directory.Delete(dir, true);
				}
			}
		}
	}

	private static void TestAutoModelRoundTrip(TestContext ctx)
	{
		AutoModelConfig model = new AutoModelConfig
		{
			Id                    = "anthropic/claude-x",
			RequestTimeoutSeconds = 900,
			Seed                  = 0,
			Temperature           = 0,
			TopP                  = 0.9m,
			TopK                  = 40,
			RepetitionPenalty     = 1.1m,
			Extras                = new List<JsonObject> { new JsonObject { ["provider"] = new JsonObject { ["order"] = new JsonArray("Anthropic") } } },
			Headers               = new List<JsonObject> { new JsonObject { ["anthropic-beta"] = "x" } }
		};

		string          json = JsonSerializer.Serialize(model, BeastJson.Persist.AutoModelConfig);
		AutoModelConfig back = JsonSerializer.Deserialize(json, BeastJson.Config.AutoModelConfig)!;

		ctx.AssertEqual( 900,    back.RequestTimeoutSeconds, "AutoModel: timeout round-trips");
		ctx.AssertEqual(  0L,              back.Seed ?? -1L, "AutoModel: a seed of zero round-trips");
		ctx.AssertEqual(  0m,       back.Temperature ?? -1m, "AutoModel: a temperature of zero round-trips");
		ctx.AssertEqual(0.9m,              back.TopP ?? -1m, "AutoModel: top_p round-trips");
		ctx.AssertEqual(  40,               back.TopK ?? -1, "AutoModel: top_k round-trips");
		ctx.AssertEqual(1.1m, back.RepetitionPenalty ?? -1m, "AutoModel: repetition penalty round-trips");
		ctx.AssertEqual(   1,       back.Extras?.Count ?? 0, "AutoModel: extras round-trip");
		ctx.AssertEqual("Anthropic", back.Extras![0]["provider"]!["order"]![0]!.GetValue<string>(), "AutoModel: extras keep their nesting");
		ctx.AssertEqual("x",                back.Headers![0]["anthropic-beta"]!.GetValue<string>(), "AutoModel: headers round-trip");
	}

	// An untouched model must not grow keys in the settings file.
	private static void TestSparseWhenUnset(TestContext ctx)
	{
		string json = JsonSerializer.Serialize(new AutoModelConfig { Id = "m" }, BeastJson.Persist.AutoModelConfig);
		ctx.Assert(!json.Contains("extras") && !json.Contains("headers"), "AutoModel: unset extras and headers are not written");
		ctx.Assert(!json.Contains("seed"), "AutoModel: an unset seed is not written");
		ctx.Assert(!json.Contains("temperature") && !json.Contains("topP") && !json.Contains("topK") && !json.Contains("minP"), "AutoModel: unset sampling is not written");
		ctx.Assert(!json.Contains("Penalty"), "AutoModel: unset penalties are not written");
	}

	private static void TestRoleFieldsRoundTrip(TestContext ctx)
	{
		Role role = new Role("Dev", "d", RoleKind.Subagent, new List<string> { "*" }, new List<string>(), "s", string.Empty, string.Empty,
			60, new Dictionary<string, int> { { "small", 30 } }, true);

		RoleService.RolesFile file = new RoleService.RolesFile { Version = 7 };
		file.Subagents.Add(role);

		string                json = JsonSerializer.Serialize(file, BeastJson.Persist.RolesFile);
		RoleService.RolesFile back = JsonSerializer.Deserialize(json, BeastJson.Config.RolesFile)!;
		Role                  got  = back.Subagents[0];

		ctx.AssertEqual( 7,             back.Version, "Roles: version round-trips");
		ctx.AssertEqual(60,             got.MaxTurns, "Roles: max_turns round-trips");
		ctx.AssertEqual(30, got.TurnBudgets["small"], "Roles: turn_budgets round-trip");
		ctx.Assert(got.NoReasoning, "Roles: no_reasoning round-trips");
	}

	private static void TestWithoutReasoningKeepsTimeout(TestContext ctx)
	{
		ModelConfig config = new ModelConfig { Id = "m", Name = "m", ReasoningEffort = "high", RequestTimeoutSeconds = 900, Seed = 7, Temperature = 0.2m, RepetitionPenalty = 1.1m };
		LlmModel    model  = new LlmModel("m", "http://localhost", string.Empty, new List<JsonObject>(), new List<JsonObject>(), config);
		LlmModel    quiet  = model.WithoutReasoning();

		ctx.AssertEqual("none",          quiet.Config.ReasoningEffort, "WithoutReasoning: thinking is off");
		ctx.AssertEqual(   900,    quiet.Config.RequestTimeoutSeconds, "WithoutReasoning: the model's timeout is kept");
		ctx.AssertEqual(    7L,              quiet.Config.Seed ?? -1L, "WithoutReasoning: the model's seed is kept");
		ctx.AssertEqual(  0.2m,       quiet.Config.Temperature ?? -1m, "WithoutReasoning: the model's temperature is kept");
		ctx.AssertEqual(  1.1m, quiet.Config.RepetitionPenalty ?? -1m, "WithoutReasoning: the model's repetition penalty is kept");
	}
}