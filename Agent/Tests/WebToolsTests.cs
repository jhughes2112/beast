using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;


public static class WebToolsTests
{
	// The parts that need no network or session, so the standalone --test harness covers them.
	public static void Test(TestContext ctx)
	{
		ctx.Log("  WebToolsTests");

		TestStripHtmlTags(ctx);
		TestProviderSelection(ctx);
		TestProviderFallback(ctx);
	}

	// A provider that answers or fails on command, so the tool's selection and fallback can be
	// exercised without touching the network.
	private class StubProvider : WebSearchProvider
	{
		private readonly string  _id;
		private readonly decimal _price;
		private readonly bool    _succeeds;
		private readonly bool    _throws;
		private readonly int     _status;

		public int Calls;

		public StubProvider(string id, decimal price, bool succeeds, bool throws)
			: this(id, price, succeeds, throws, 0)
		{
		}

		public StubProvider(string id, decimal price, bool succeeds, bool throws, int status)
		{
			_id       = id;
			_price    = price;
			_succeeds = succeeds;
			_throws   = throws;
			_status   = status;
			Calls     = 0;
		}

		public override string  Id               => _id;
		public override string  DisplayName      => _id;
		public override string  Domain           => _id + ".test";
		public override decimal PricePerThousand => _price;
		public override string  DefaultModel     => "stub";

		public override Task<WebSearchAnswer> SearchAsync(string query, string goal, string systemPrompt, string apiKey, string model, int maxOutputTokens, CancellationToken ct)
		{
			Calls++;
			if (_throws)
				throw new InvalidOperationException($"{_id} exploded");
			if (_succeeds)
				return Task.FromResult(WebSearchAnswer.Success($"answer from {_id}", SearchFee));
			if (_status > 0)
				return Task.FromResult(WebSearchAnswer.FailureHttp($"{_id} said no", 0m, _status));
			return Task.FromResult(WebSearchAnswer.Failure($"{_id} said no", 0m));
		}
	}

	private static void TestProviderFallback(TestContext ctx)
	{
		Session parent = NewParentSession();

		// Cheapest first, and a provider that fails hands off to the next rather than failing the
		// whole call. The throwing provider must be contained the same way an error return is.
		StubProvider cheapBroken = new StubProvider("cheap", 1m, false, false);
		StubProvider midThrows   = new StubProvider("mid",   2m, false,  true);
		StubProvider dearWorks   = new StubProvider("dear",  3m,  true, false);
		List<(WebSearchProvider Provider, string ApiKey, string Model)> providers = new List<(WebSearchProvider, string, string)>
		{
			(cheapBroken, "k", "stub"),
			(  midThrows, "k", "stub"),
			(  dearWorks, "k", "stub")
		};

		ToolResult result = new WebSearchTool(providers, "test").SearchAsync("id", "q", "g", parent, new TestCaptureTransport(), "ws-test", 0, CancellationToken.None).GetAwaiter().GetResult();

		ctx.AssertEqual(0, result.ExitCode, "WebSearchTool: falls through to a provider that works");
		ctx.AssertContains(result.StdOut, "answer from dear",  "WebSearchTool: returns the working provider's answer");
		ctx.AssertContains(result.StdOut, "searched via dear", "WebSearchTool: names the provider that answered");
		ctx.AssertEqual(1, cheapBroken.Calls, "WebSearchTool: cheapest is tried first");
		ctx.AssertEqual(1,   midThrows.Calls, "WebSearchTool: a throwing provider is contained, not fatal");
		ctx.AssertEqual(1,   dearWorks.Calls, "WebSearchTool: the working provider is reached");
		ctx.AssertEqual(0.003m, parent.Data.TotalCost, "WebSearchTool: the answering provider's fee rolls up to the caller");

		// Every provider failing is a tool error naming each reason, not an exception.
		StubProvider onlyBroken = new StubProvider("broken", 1m, false, false);
		List<(WebSearchProvider Provider, string ApiKey, string Model)> allBroken = new List<(WebSearchProvider, string, string)>
		{
			(onlyBroken, "k", "stub")
		};
		ToolResult failed = new WebSearchTool(allBroken, "test").SearchAsync("id", "q", "g", NewParentSession(), new TestCaptureTransport(), "ws-test", 0, CancellationToken.None).GetAwaiter().GetResult();
		ctx.AssertEqual(1, failed.ExitCode, "WebSearchTool: all-providers-failed is an error result");
		ctx.AssertContains(failed.StdErr, "broken said no", "WebSearchTool: the failure names why each provider failed");

		// Credits exhausted on one provider: the human is alerted (a fallback cannot fix a billing
		// problem) AND the search still completes on the next provider.
		StubProvider         brokeProvider  = new StubProvider("broke", 1m, false, false, 402);
		StubProvider         payingProvider = new StubProvider("paying", 2m, true, false);
		TestCaptureTransport transport      = new TestCaptureTransport();
		List<(WebSearchProvider Provider, string ApiKey, string Model)> creditCase = new List<(WebSearchProvider, string, string)>
		{
			( brokeProvider, "k", "stub"),
			(payingProvider, "k", "stub")
		};
		WebSearchTool creditTool = new WebSearchTool(creditCase, "test");
		ToolResult    recovered  = creditTool.SearchAsync("id", "q", "g", NewParentSession(), transport, "s", 0, CancellationToken.None).GetAwaiter().GetResult();

		ctx.AssertEqual(0, recovered.ExitCode, "WebSearchTool: a 402 still falls back to the next provider");
		ctx.AssertContains(recovered.StdOut, "answer from paying", "WebSearchTool: the fallback provider's answer is returned");
		int alerts = 0;
		foreach ((FrameType type, string text) in transport.Sent)
		{
			if (type == FrameType.Alert && text.Contains("out of credits", StringComparison.Ordinal))
				alerts++;
		}
		ctx.AssertEqual(1, alerts, "WebSearchTool: an exhausted-credits 402 raises a client alert");

		// A second search must not re-raise the banner for the same provider.
		creditTool.SearchAsync("id", "q", "g", NewParentSession(), transport, "s", 0, CancellationToken.None).GetAwaiter().GetResult();
		int repeated = 0;
		foreach ((FrameType type, string text) in transport.Sent)
		{
			if (type == FrameType.Alert && text.Contains("out of credits", StringComparison.Ordinal))
				repeated++;
		}
		ctx.AssertEqual(1, repeated, "WebSearchTool: the credit alert is raised once per provider, not per search");

		// No providers at all (nothing configured, or no keys resolved) is also a clean error.
		ToolResult none = new WebSearchTool(new List<(WebSearchProvider, string, string)>(), "test").SearchAsync("id", "q", "g", NewParentSession(), new TestCaptureTransport(), "ws-test", 0, CancellationToken.None).GetAwaiter().GetResult();
		ctx.AssertEqual(1, none.ExitCode, "WebSearchTool: no configured provider is an error result");
	}

	private static Session NewParentSession()
	{
		BeastSession data = new BeastSession("ws-test", "ws-test", "model", "role", string.Empty,
			new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		return new Session(data, string.Empty, new TestCaptureTransport(), false);
	}

	public static async Task TestAsync(TestContext ctx, BeastSettings settings, RoleService roleService, ITransportServer transport, Session parent, CancellationToken cancellationToken)
	{
		Test(ctx);

		List<(WebSearchProvider Provider, string ApiKey, string Model)> usable = WebSearchRegistry.ResolveUsable(settings);
		if (usable.Count > 0)
		{
			await TestWebSearchAsync(ctx, usable, parent, cancellationToken);
		}
		else
		{
			ctx.Log("  WebToolsTests: skipping live web search test (no provider enabled with a resolvable key)");
		}
	}

	// Key resolution and cheapest-first ordering, with no network involved.
	private static void TestProviderSelection(TestContext ctx)
	{
		BeastSettings settings = new BeastSettings();
		settings.Auto.Add(new AutoProviderConfig { BaseUrl = "https://openrouter.ai/api/v1/chat/completions", ApiKey = "or-key" });
		settings.Providers.Add(new ProviderConfig { BaseUrl = "https://api.anthropic.com/v1/messages", ApiKey = "ant-key" });
		settings.WebSearch = new WebSearchConfig();
		settings.WebSearch.Providers.Add(new WebSearchProviderConfig { Provider = "anthropic", Enabled = true });
		settings.WebSearch.Providers.Add(new WebSearchProviderConfig { Provider = "openrouter", Enabled = true });
		// Enabled but no endpoint carries its domain — must be dropped, not attempted.
		settings.WebSearch.Providers.Add(new WebSearchProviderConfig { Provider = "gemini", Enabled = true });
		// Configured with a key available, but the user turned it off.
		settings.WebSearch.Providers.Add(new WebSearchProviderConfig { Provider = "perplexity", Enabled = false });

		List<(WebSearchProvider Provider, string ApiKey, string Model)> usable = WebSearchRegistry.ResolveUsable(settings);
		ctx.AssertEqual(2, usable.Count, "WebSearch: only enabled providers with resolvable keys are usable");
		ctx.AssertEqual("openrouter", usable[0].Provider.Id, "WebSearch: cheapest provider comes first");
		ctx.AssertEqual("or-key",          usable[0].ApiKey, "WebSearch: key resolved from the matching auto endpoint");
		ctx.AssertEqual("anthropic",  usable[1].Provider.Id, "WebSearch: dearer provider ranks second");
		ctx.AssertEqual("ant-key",         usable[1].ApiKey, "WebSearch: key resolved from the matching manual provider");
		ctx.AssertEqual(usable[0].Provider.DefaultModel, usable[0].Model, "WebSearch: blank model override falls back to the provider default");

		// A domain with no key at all resolves to empty rather than throwing.
		WebSearchProvider? gemini = WebSearchRegistry.Find("gemini");
		ctx.AssertNotNull(gemini, "WebSearch: gemini provider is registered");
		ctx.AssertEqual(string.Empty, WebSearchRegistry.ResolveApiKey(settings, gemini!), "WebSearch: unmatched domain yields no key");

		// The legacy openrouter block still supplies a key and an implicit entry for upgraders.
		BeastSettings legacy = new BeastSettings();
		legacy.WebSearch     = new WebSearchConfig
		{
			Openrouter = new OpenrouterSearchConfig { ApiKey = "legacy-key", Enabled = true, Model = "openrouter/free" }
		};
		List<(WebSearchProvider Provider, string ApiKey, string Model)> fromLegacy = WebSearchRegistry.ResolveUsable(legacy);
		ctx.AssertEqual(1, fromLegacy.Count, "WebSearch: legacy openrouter block still yields a usable provider");
		ctx.AssertEqual("legacy-key", fromLegacy[0].ApiKey, "WebSearch: legacy api key is honored");

		// A placeholder key is not a key.
		BeastSettings placeholder = new BeastSettings();
		placeholder.WebSearch     = new WebSearchConfig
		{
			Openrouter = new OpenrouterSearchConfig { ApiKey = "YOUR_OPENROUTER_KEY_HERE", Enabled = true }
		};
		ctx.AssertEqual(0, WebSearchRegistry.ResolveUsable(placeholder).Count, "WebSearch: placeholder key does not enable a provider");
	}

	private static async Task TestWebSearchAsync(TestContext ctx, List<(WebSearchProvider Provider, string ApiKey, string Model)> usable, Session parent, CancellationToken cancellationToken)
	{
		ctx.Log($"  WebToolsTests: testing live web search via {usable[0].Provider.DisplayName}");

		try
		{
			WebSearchTool searcher = new WebSearchTool(usable, string.Empty);

			using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
			using CancellationTokenSource cts        = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
			ToolResult result                        = await searcher.SearchAsync("testSearchId", "What is the capital of France?", "State the capital city of France in one short sentence.", parent, new TestCaptureTransport(), parent.Id, 0, cts.Token);

			string response = result.ExitCode == 0 ? result.StdOut : result.StdErr;
			ctx.Log($"    response: {response}");
			ctx.Assert(result.ExitCode == 0 && !string.IsNullOrEmpty(result.StdOut), "WebSearch: no error returned");
			ctx.Assert(response.Length > 10, "WebSearch: non-empty response");
		}
		catch (OperationCanceledException)
		{
			ctx.Log("    TIMEOUT: web search timed out");
		}
		catch (Exception ex)
		{
			ctx.Log($"    ERROR: {ex}");
		}
	}

	private static void TestStripHtmlTags(TestContext ctx)
	{
		Type[] types = new Type[] { typeof(string) };

		// Basic tag stripping.
		string basic = (string)Reflect.Static(typeof(WebFetch), "StripHtmlTags", types, new object[] { "<p>Hello World</p>" })!;
		ctx.Assert(basic.Contains("Hello World"), "StripHtmlTags: basic tags stripped");
		ctx.Assert(       !basic.Contains("<p>"), "StripHtmlTags: no tags remain");

		// Script tags removed entirely.
		string script = (string)Reflect.Static(typeof(WebFetch), "StripHtmlTags", types, new object[] { "before<script>alert('xss')</script>after" })!;
		ctx.Assert(!script.Contains("alert"), "StripHtmlTags: script content removed");
		ctx.Assert(script.Contains("before"), "StripHtmlTags: text before script preserved");
		ctx.Assert( script.Contains("after"), "StripHtmlTags: text after script preserved");

		// Style tags removed entirely.
		string style = (string)Reflect.Static(typeof(WebFetch), "StripHtmlTags", types, new object[] { "text<style>.x{color:red}</style>more" })!;
		ctx.Assert(!style.Contains("color"), "StripHtmlTags: style content removed");
		ctx.Assert(  style.Contains("text"), "StripHtmlTags: text around style preserved");

		// HTML entity decoding.
		string entity = (string)Reflect.Static(typeof(WebFetch), "StripHtmlTags", types, new object[] { "<span>&amp; &lt; &gt;</span>" })!;
		ctx.Assert(entity.Contains("&"), "StripHtmlTags: &amp; decoded");
		ctx.Assert(entity.Contains("<"), "StripHtmlTags: &lt; decoded");

		// Whitespace collapsing.
		string spaces = (string)Reflect.Static(typeof(WebFetch), "StripHtmlTags", types, new object[] { "<div>  hello   world  </div>" })!;
		ctx.Assert(!spaces.Contains("  "), "StripHtmlTags: multiple spaces collapsed");

		// Empty input.
		string empty = (string)Reflect.Static(typeof(WebFetch), "StripHtmlTags", types, new object[] { "" })!;
		ctx.AssertEqual("", empty, "StripHtmlTags: empty input");

		// Nested tags.
		string nested = (string)Reflect.Static(typeof(WebFetch), "StripHtmlTags", types, new object[] { "<div><p><b>deep</b></p></div>" })!;
		ctx.Assert(nested.Contains("deep"), "StripHtmlTags: nested tags stripped");
		ctx.Assert(  !nested.Contains("<"), "StripHtmlTags: no angle brackets in output");
	}

}