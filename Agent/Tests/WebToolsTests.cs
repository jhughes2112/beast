using System.Reflection;
using System;


public static class WebToolsTests
{
    public static void Test(TestContext ctx)
    {
        ctx.Log("  WebToolsTests");

        TestStripHtmlTags(ctx);

        ctx.Log("  WebToolsTests: skipping web search test (no LLM proxy configured)");
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
