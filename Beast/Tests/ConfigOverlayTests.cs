using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;


// Drives the /config model list through its real key handling and reads what it would save: the
// /config-apply payload is the only thing that decides what survives leaving the page.
public static class ConfigOverlayTests
{
	private const string Catalog =
		"{\"kind\":\"catalog\",\"baseUrl\":\"http://x/v1\",\"models\":[" +
		"{\"id\":\"plain\",\"name\":\"plain\",\"contextWindow\":1000,\"costInput\":0,\"costOutput\":0,\"modalities\":[\"text\"]}]}";

	public static void Test(TestContext ctx)
	{
		ctx.Log("  ConfigOverlayTests");
		TestEditWithoutToggleIsSaved(ctx);
		TestDisabledToggleIsSaved(ctx);
		TestClearingNothingAddsNoEntry(ctx);
		TestRetainDefaultsOn(ctx);
		TestForgetClearsEveryOverride(ctx);
		TestSeed(ctx);
		TestSampling(ctx);
		TestFailedTestStaysOnPage(ctx);
		TestCancelledTestAndUnchangedPage(ctx);
	}

	// A failed test keeps the user on the page, with nothing saved and the reason shown; Esc again leaves.
	private static void TestFailedTestStaysOnPage(TestContext ctx)
	{
		List<string>  sent    = new List<string>();
		ConfigOverlay overlay = OpenCatalog(sent);

		Press(overlay, ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Enter);
		Type(overlay, "5000");
		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);

		JsonNode? request = LastTest(sent);
		ctx.AssertNotNull(request, "ConfigOverlay: leaving changed settings sends a test");
		ctx.AssertEqual(5000, request?["model"]?["contextWindow"]?.GetValue<int>() ?? 0, "ConfigOverlay: of the edited settings");
		ctx.Assert(SavedEntry(sent, "plain") == null, "ConfigOverlay: nothing is saved while it runs");

		Answer(overlay, sent, "HTTP 400: nope");
		Press(overlay, ConsoleKey.Escape);
		ctx.Assert(SavedEntry(sent, "plain") == null, "ConfigOverlay: a failed test stays on the page, so that Esc only left the page");

		// Now on the model list, having chosen to leave anyway: one test, not a second.
		int tests = CountTests(sent);
		ctx.AssertEqual(1, tests, "ConfigOverlay: leaving after a failure does not test the same settings again");
		Press(overlay, ConsoleKey.Escape);
		ctx.AssertEqual(5000, SavedEntry(sent, "plain")?["contextWindow"]?.GetValue<int>() ?? 0, "ConfigOverlay: leaving anyway keeps the edit");

		// A failure followed by a further change is tested again.
		sent    = new List<string>();
		overlay = OpenCatalog(sent);
		Press(overlay, ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Enter);
		Type(overlay, "5000");
		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);
		Answer(overlay, sent, "HTTP 400: nope");
		Press(overlay, ConsoleKey.Enter);
		Type(overlay, "0");
		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);
		ctx.AssertEqual(2, CountTests(sent), "ConfigOverlay: a change after a failure is tested again");
	}

	// Giving up on a test stays on the page, ignores the late answer, and Esc again leaves. A page with no
	// change sends no test at all.
	private static void TestCancelledTestAndUnchangedPage(TestContext ctx)
	{
		List<string>  sent    = new List<string>();
		ConfigOverlay overlay = OpenCatalog(sent);

		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);
		ctx.AssertEqual(0, CountTests(sent), "ConfigOverlay: an unchanged page sends no test");

		sent    = new List<string>();
		overlay = OpenCatalog(sent);
		Press(overlay, ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Enter);
		Type(overlay, "5000");
		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);
		JsonNode? stale = LastTest(sent);
		Press(overlay, ConsoleKey.Escape);
		overlay.OnConfigFrame(new JsonObject { ["kind"] = "test-result", ["id"] = stale?["id"]?.GetValue<int>() ?? 0, ["error"] = string.Empty }.ToJsonString());
		Press(overlay, ConsoleKey.Escape);
		Press(overlay, ConsoleKey.Escape);
		ctx.AssertEqual(1, CountTests(sent), "ConfigOverlay: leaving after giving up does not test again");
		ctx.AssertEqual(5000, SavedEntry(sent, "plain")?["contextWindow"]?.GetValue<int>() ?? 0, "ConfigOverlay: and keeps the edit");
	}

	// Esc on an edited settings page, with the test it starts passing.
	private static void LeavePassing(ConfigOverlay overlay, List<string> sent)
	{
		Press(overlay, ConsoleKey.Escape);
		Answer(overlay, sent, string.Empty);
	}

	private static void Answer(ConfigOverlay overlay, List<string> sent, string error)
	{
		overlay.OnConfigFrame(new JsonObject { ["kind"] = "test-result", ["id"] = LastTest(sent)?["id"]?.GetValue<int>() ?? 0, ["error"] = error }.ToJsonString());
	}

	private static JsonNode? LastTest(List<string> sent)
	{
		const string Prefix = "/config-test ";
		JsonNode? last = null;
		foreach (string command in sent)
		{
			if (command.StartsWith(Prefix, StringComparison.Ordinal))
				last = JsonNode.Parse(command.Substring(Prefix.Length));
		}
		return last;
	}

	private static int CountTests(List<string> sent)
	{
		int count = 0;
		foreach (string command in sent)
		{
			if (command.StartsWith("/config-test ", StringComparison.Ordinal))
				count++;
		}
		return count;
	}

	// Sampling rows follow the seed: temperature, top P, top K. Zero is a real value, top K must be
	// whole, and clearing a row loaded from settings stops sending it.
	private static void TestSampling(TestContext ctx)
	{
		List<string>  sent    = new List<string>();
		ConfigOverlay overlay = OpenCatalog(sent);

		Press(overlay, ConsoleKey.Enter);
		for (int i = 0; i < 9; i++)
			Press(overlay, ConsoleKey.DownArrow);
		Press(overlay, ConsoleKey.Enter);
		Type(overlay, "NaN");
		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);
		Press(overlay,  ConsoleKey.Enter);
		Type(overlay, "0");
		Press(overlay,     ConsoleKey.Enter);
		Press(overlay, ConsoleKey.DownArrow);
		Press(overlay, ConsoleKey.DownArrow);
		Press(overlay,     ConsoleKey.Enter);
		Type(overlay, "1.5");
		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);
		Press(overlay,  ConsoleKey.Enter);
		Type(overlay, "40");
		Press(overlay, ConsoleKey.Enter);
		LeavePassing(overlay, sent);
		Press(overlay, ConsoleKey.Escape);

		JsonObject? entry = SavedEntry(sent, "plain");
		ctx.AssertEqual(0m, entry?["temperature"]?.GetValue<decimal>() ?? -1m, "ConfigOverlay: a temperature of zero is saved, and NaN was refused");
		ctx.AssertEqual(40,             entry?["topK"]?.GetValue<int>() ?? -1, "ConfigOverlay: top K is saved whole, and 1.5 was refused");
		ctx.Assert(entry?["topP"] == null, "ConfigOverlay: an untouched sampling field is not saved");

		sent    = new List<string>();
		overlay = OpenCatalog(sent);
		overlay.OnConfigFrame(Catalog.Replace("\"modalities\":[\"text\"]}", "\"modalities\":[\"text\"],\"configured\":true,\"override\":{\"temperature\":0.7,\"topP\":0.9}}", StringComparison.Ordinal));
		Press(overlay, ConsoleKey.Enter);
		for (int i = 0; i < 9; i++)
			Press(overlay, ConsoleKey.DownArrow);
		Press(overlay, ConsoleKey.Delete);
		LeavePassing(overlay, sent);
		Press(overlay, ConsoleKey.Escape);

		entry = SavedEntry(sent, "plain");
		ctx.Assert(entry?["temperature"] == null, "ConfigOverlay: clearing temperature stops sending it");
		ctx.AssertEqual(0.9m, entry?["topP"]?.GetValue<decimal>() ?? -1m, "ConfigOverlay: a loaded value is kept through an apply");
	}

	// Blank sends no seed, zero is a real one, and anything that is not a whole number is refused.
	private static void TestSeed(TestContext ctx)
	{
		List<string>  sent    = new List<string>();
		ConfigOverlay overlay = OpenCatalog(sent);

		Press(overlay, ConsoleKey.Enter);
		for (int i = 0; i < 8; i++)
			Press(overlay, ConsoleKey.DownArrow);
		Press(overlay, ConsoleKey.Enter);
		Type(overlay, "abc");
		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);
		Press(overlay,  ConsoleKey.Enter);
		Type(overlay, "0");
		Press(overlay, ConsoleKey.Enter);
		LeavePassing(overlay, sent);
		Press(overlay, ConsoleKey.Escape);

		JsonObject? entry = SavedEntry(sent, "plain");
		ctx.AssertEqual(0L, entry?["seed"]?.GetValue<long>() ?? -1L, "ConfigOverlay: a seed of zero is saved");

		sent    = new List<string>();
		overlay = OpenCatalog(sent);
		overlay.OnConfigFrame(Catalog.Replace("\"modalities\":[\"text\"]}", "\"modalities\":[\"text\"],\"configured\":true,\"override\":{\"seed\":7}}", StringComparison.Ordinal));
		Press(overlay, ConsoleKey.Enter);
		for (int i = 0; i < 8; i++)
			Press(overlay, ConsoleKey.DownArrow);
		Press(overlay, ConsoleKey.Delete);
		LeavePassing(overlay, sent);
		Press(overlay, ConsoleKey.Escape);

		entry = SavedEntry(sent, "plain");
		ctx.AssertNotNull(entry, "ConfigOverlay: a configured model stays in the apply");
		ctx.Assert(entry?["seed"] == null, "ConfigOverlay: clearing the seed stops sending it");

		// An endpoint whose protocol has no seed refuses one, and the old value can still be cleared.
		sent    = new List<string>();
		overlay = OpenCatalog(sent);
		overlay.OnConfigFrame(Catalog.Replace("\"baseUrl\":\"http://x/v1\",", "\"baseUrl\":\"http://x/v1\",\"seedSupported\":false,", StringComparison.Ordinal));
		Press(overlay, ConsoleKey.Enter);
		for (int i = 0; i < 8; i++)
			Press(overlay, ConsoleKey.DownArrow);
		Press(overlay, ConsoleKey.Enter);
		Type(overlay, "5");
		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);
		LeavePassing(overlay, sent);
		Press(overlay, ConsoleKey.Escape);
		ctx.Assert(SavedEntry(sent, "plain")?["seed"] == null, "ConfigOverlay: an endpoint with no seed parameter refuses one");
	}

	// Editing a model that was never enabled, then leaving, keeps the edit without enabling the model.
	private static void TestEditWithoutToggleIsSaved(TestContext ctx)
	{
		List<string>  sent    = new List<string>();
		ConfigOverlay overlay = OpenCatalog(sent);

		Press(overlay, ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Enter);
		Type(overlay, "5000");
		Press(overlay, ConsoleKey.Enter);
		LeavePassing(overlay, sent);
		Press(overlay, ConsoleKey.Escape);

		JsonObject? entry = SavedEntry(sent, "plain");
		ctx.AssertNotNull(entry, "ConfigOverlay: an edit on a never-enabled model is saved");
		ctx.AssertEqual( 5000, entry?["contextWindow"]?.GetValue<int>() ?? 0, "ConfigOverlay: with the edited value");
		ctx.AssertEqual(false,   entry?["enabled"]?.GetValue<bool>() ?? true, "ConfigOverlay: and the model stays off");
	}

	// The toggled fields take the same path as typed ones.
	private static void TestDisabledToggleIsSaved(TestContext ctx)
	{
		List<string>  sent    = new List<string>();
		ConfigOverlay overlay = OpenCatalog(sent);

		Press(overlay, ConsoleKey.Enter);
		for (int i = 0; i < 6; i++)
			Press(overlay, ConsoleKey.DownArrow);
		Press(overlay, ConsoleKey.Enter);
		LeavePassing(overlay, sent);
		Press(overlay, ConsoleKey.Escape);

		JsonObject? entry = SavedEntry(sent, "plain");
		ctx.AssertNotNull(entry, "ConfigOverlay: toggling a field on a never-enabled model is saved");
		ctx.AssertEqual(false, entry?["retainReasoning"]?.GetValue<bool>() ?? true, "ConfigOverlay: retain reasoning turned off");
	}

	// Del on a field with nothing set leaves nothing to keep, so it must not invent a settings entry.
	private static void TestClearingNothingAddsNoEntry(TestContext ctx)
	{
		List<string>  sent    = new List<string>();
		ConfigOverlay overlay = OpenCatalog(sent);

		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Delete);
		Press(overlay, ConsoleKey.Escape);
		Press(overlay, ConsoleKey.Escape);

		ctx.Assert(SavedEntry(sent, "plain") == null, "ConfigOverlay: clearing an unset field adds no entry");
	}

	// A row with no settings entry follows the default: enabling it must not write retainReasoning false.
	private static void TestRetainDefaultsOn(TestContext ctx)
	{
		List<string>  sent    = new List<string>();
		ConfigOverlay overlay = OpenCatalog(sent);

		Press(overlay, ConsoleKey.Spacebar);
		Press(overlay,   ConsoleKey.Escape);

		JsonObject? entry = SavedEntry(sent, "plain");
		ctx.AssertEqual(true, entry?["enabled"]?.GetValue<bool>() ?? false, "ConfigOverlay: space enables the model");
		ctx.Assert(entry?["retainReasoning"] == null, "ConfigOverlay: a new model keeps the retain-reasoning default");
	}

	// Forgetting drops every override, so enabling the model again starts clean.
	private static void TestForgetClearsEveryOverride(TestContext ctx)
	{
		List<string>  sent    = new List<string>();
		ConfigOverlay overlay = OpenCatalog(sent);

		Press(overlay, ConsoleKey.Enter);
		for (int i = 0; i < 7; i++)
			Press(overlay, ConsoleKey.DownArrow);
		Press(overlay, ConsoleKey.Enter);
		Type(overlay, "900");
		Press(overlay, ConsoleKey.Enter);
		LeavePassing(overlay, sent);
		Press(overlay,   ConsoleKey.Delete);
		Press(overlay, ConsoleKey.Spacebar);
		Press(overlay,   ConsoleKey.Escape);

		JsonObject? entry = SavedEntry(sent, "plain");
		ctx.AssertEqual(true, entry?["enabled"]?.GetValue<bool>() ?? false, "ConfigOverlay: a forgotten model can be enabled again");
		ctx.Assert(entry?["requestTimeoutSeconds"] == null, "ConfigOverlay: forgetting clears the timeout");
	}

	private static ConfigOverlay OpenCatalog(List<string> sent)
	{
		ConfigOverlay overlay = new ConfigOverlay(sent.Add);
		overlay.Open();
		overlay.OnConfigFrame(Catalog);
		return overlay;
	}

	private static void Press(ConfigOverlay overlay, ConsoleKey key)
	{
		overlay.HandleKey(new ConsoleKeyInfo(key == ConsoleKey.Spacebar ? ' ' : '\0', key, false, false, false));
	}

	private static void Type(ConfigOverlay overlay, string text)
	{
		foreach (char c in text)
			overlay.HandleKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
	}

	// The model's entry in the apply that was sent; null when no apply went out or it left the model out.
	private static JsonObject? SavedEntry(List<string> sent, string id)
	{
		const string Prefix = "/config-apply ";
		JsonObject? entry = null;
		foreach (string command in sent)
		{
			if (command.StartsWith(Prefix, StringComparison.Ordinal))
			{
				JsonArray? models = JsonNode.Parse(command.Substring(Prefix.Length))?["models"]?.AsArray();
				foreach (JsonNode? model in models ?? new JsonArray())
				{
					if (model?["id"]?.GetValue<string>() == id)
						entry = model.AsObject();
				}
			}
		}
		return entry;
	}
}