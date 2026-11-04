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
	}

	// Editing a model that was never enabled, then leaving, keeps the edit without enabling the model.
	private static void TestEditWithoutToggleIsSaved(TestContext ctx)
	{
		List<string>  sent    = new List<string>();
		ConfigOverlay overlay = OpenCatalog(sent);

		Press(overlay, ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Enter);
		Type(overlay, "5000");
		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);
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
		Press(overlay,  ConsoleKey.Enter);
		Press(overlay, ConsoleKey.Escape);
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
		Press(overlay,    ConsoleKey.Enter);
		Press(overlay,   ConsoleKey.Escape);
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