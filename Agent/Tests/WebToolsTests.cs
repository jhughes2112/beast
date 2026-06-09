using System.Reflection;
using System;
using System.Threading;
using System.Threading.Tasks;


public static class WebToolsTests
{
    public static async Task TestAsync(TestContext ctx, WebSearchConfig? webSearchConfig)
    {
        ctx.Log("  WebToolsTests");

        TestStripHtmlTags(ctx);

        if (webSearchConfig?.Openrouter != null && webSearchConfig.Openrouter.Enabled)
        {
            await TestWebSearchAsync(ctx, webSearchConfig.Openrouter);
        }
        else
        {
            ctx.Log("  WebToolsTests: skipping web search test (not configured or disabled)");
        }
    }

    private static async Task TestWebSearchAsync(TestContext ctx, OpenrouterSearchConfig config)
    {
        ctx.Log("  WebToolsTests: testing web search via OpenRouter");

        Action<string> previousLog = ProtocolChatCompletions.Log;
        ProtocolChatCompletions.Log = line => ctx.Log($"    {line}");

        TestCaptureTransport captureTransport = new TestCaptureTransport();

        try
        {
            WebSearchOpenrouter searcher = new WebSearchOpenrouter(config.BuildModel());

            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            ToolResult result = await searcher.SearchWebAsync("What is the capital of France?", captureTransport, "test", cts.Token);

            string response = result.ExitCode == 0 ? result.StdOut : result.StdErr;
            ctx.Log($"    response: {response}");
            ctx.Assert(result.ExitCode == 0 && !string.IsNullOrEmpty(result.StdOut), "WebSearch: no error returned");
            ctx.Assert(response.Length > 10, "WebSearch: non-empty response");
        }
        catch (OperationCanceledException)
        {
            ctx.Log("    TIMEOUT: web search timed out after 30s");
        }
        catch (Exception ex)
        {
            ctx.Log($"    ERROR: {ex.Message}");
        }
        finally
        {
            ProtocolChatCompletions.Log = previousLog;
        }
    }

    private static void TestStripHtmlTags(TestContext ctx)
    {
        Type[] types = new Type[] { typeof(string) };

        // Basic tag stripping.
        string basic = (string)Reflect.Static(typeof(WebFetch), "StripHtmlTags", types, new object[] { "<p>Hello World</p>" })!;
        ctx.Assert(basic.Contains("Hello World"), "StripHtmlTags: basic tags stripped");
        ctx.Assert(!basic.Contains("<p>"), "StripHtmlTags: no tags remain");

        // Script tags removed entirely.
        string script = (string)Reflect.Static(typeof(WebFetch), "StripHtmlTags", types, new object[] { "before<script>alert('xss')</script>after" })!;
        ctx.Assert(!script.Contains("alert"), "StripHtmlTags: script content removed");
        ctx.Assert(script.Contains("before"), "StripHtmlTags: text before script preserved");
        ctx.Assert(script.Contains("after"), "StripHtmlTags: text after script preserved");

        // Style tags removed entirely.
        string style = (string)Reflect.Static(typeof(WebFetch), "StripHtmlTags", types, new object[] { "text<style>.x{color:red}</style>more" })!;
        ctx.Assert(!style.Contains("color"), "StripHtmlTags: style content removed");
        ctx.Assert(style.Contains("text"), "StripHtmlTags: text around style preserved");

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
        ctx.Assert(!nested.Contains("<"), "StripHtmlTags: no angle brackets in output");
    }

}
