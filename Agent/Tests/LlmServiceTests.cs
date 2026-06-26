using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


public static class LlmServiceTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  LlmServiceTests");

		TestEpochToSecondsFromNow(ctx);
		TestGetFirstHeaderValue(ctx);
		TestIsRateLimited(ctx);
		TestParseRateLimitSecondsFromErrorBody(ctx);
		TestTryAdaptToError(ctx);
	}

	private static void TestEpochToSecondsFromNow(TestContext ctx)
	{
		Type[] types = [typeof(long)];

		// Future epoch should return positive seconds.
		long futureEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
		int futureResult = (int)Reflect.Static(typeof(ProtocolHelpers), "EpochToSecondsFromNow", types, [futureEpoch])!;
		ctx.Assert(futureResult >= 59 && futureResult <= 62, "EpochToSecondsFromNow: future epoch returns ~61");

		// Past epoch should return 0.
		long pastEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
		int pastResult = (int)Reflect.Static(typeof(ProtocolHelpers), "EpochToSecondsFromNow", types, [pastEpoch])!;
		ctx.AssertEqual(0, pastResult, "EpochToSecondsFromNow: past epoch returns 0");

		// Millisecond epoch should be normalized to seconds.
		long msEpoch = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120) * 1000;
		int msResult = (int)Reflect.Static(typeof(ProtocolHelpers), "EpochToSecondsFromNow", types, [msEpoch])!;
		ctx.Assert(msResult >= 119 && msResult <= 122, "EpochToSecondsFromNow: millisecond epoch normalized");
	}

	private static void TestGetFirstHeaderValue(TestContext ctx)
	{
		Type[] types = [typeof(HttpResponseHeaders), typeof(string)];

		HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
		response.Headers.TryAddWithoutValidation("X-Custom", "value1");

		string? found = (string?)Reflect.Static(typeof(ProtocolHelpers), "GetFirstHeaderValue", types, [response.Headers, "X-Custom"]);
		ctx.AssertEqual("value1", found, "GetFirstHeaderValue: finds present header");

		string? missing = (string?)Reflect.Static(typeof(ProtocolHelpers), "GetFirstHeaderValue", types, [response.Headers, "X-Missing"]);
		ctx.AssertNull(missing, "GetFirstHeaderValue: returns null for missing header");
	}

	private static void TestIsRateLimited(TestContext ctx)
	{
		Type[] types = [typeof(HttpResponseMessage), typeof(string)];

		// 429 status code.
		HttpResponseMessage r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
		bool is429 = (bool)Reflect.Static(typeof(ProtocolHelpers), "IsRateLimited", types, [r429, ""])!;
		ctx.Assert(is429, "IsRateLimited: 429 status detected");

		// Retry-After header present.
		HttpResponseMessage rRetry = new HttpResponseMessage(HttpStatusCode.OK);
		rRetry.Headers.TryAddWithoutValidation("Retry-After", "30");
		bool isRetry = (bool)Reflect.Static(typeof(ProtocolHelpers), "IsRateLimited", types, [rRetry, ""])!;
		ctx.Assert(isRetry, "IsRateLimited: Retry-After header detected");

		// Normal 200 is not rate limited.
		HttpResponseMessage rOk = new HttpResponseMessage(HttpStatusCode.OK);
		bool isOk = (bool)Reflect.Static(typeof(ProtocolHelpers), "IsRateLimited", types, [rOk, ""])!;
		ctx.Assert(!isOk, "IsRateLimited: normal 200 not rate limited");

		// Body containing code 429.
		HttpResponseMessage rBody = new HttpResponseMessage(HttpStatusCode.OK);
		bool isBody = (bool)Reflect.Static(typeof(ProtocolHelpers), "IsRateLimited", types, [rBody, "{\"code\":429}"])!;
		ctx.Assert(isBody, "IsRateLimited: body code 429 detected");

		// X-RateLimit-Remaining = 0.
		HttpResponseMessage rRemaining = new HttpResponseMessage(HttpStatusCode.OK);
		rRemaining.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
		bool isRemaining = (bool)Reflect.Static(typeof(ProtocolHelpers), "IsRateLimited", types, [rRemaining, ""])!;
		ctx.Assert(isRemaining, "IsRateLimited: X-RateLimit-Remaining 0 detected");
	}

	private static void TestParseRateLimitSecondsFromErrorBody(TestContext ctx)
	{
		Type[] types = [typeof(string)];

		// Valid nested error body with X-RateLimit-Reset.
		long futureEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
		string validBody = $"{{\"error\":{{\"metadata\":{{\"headers\":{{\"X-RateLimit-Reset\":\"{futureEpoch}\"}}}}}}}}";
		int validResult = (int)Reflect.Static(typeof(ProtocolHelpers), "ParseRateLimitSecondsFromBody", types, [validBody])!;
		ctx.Assert(validResult >= 59 && validResult <= 62, "ParseRateLimitSecondsFromBody: valid body parsed");

		// Invalid JSON returns 0.
		int invalidResult = (int)Reflect.Static(typeof(ProtocolHelpers), "ParseRateLimitSecondsFromBody", types, ["not json at all"])!;
		ctx.AssertEqual(0, invalidResult, "ParseRateLimitSecondsFromBody: invalid JSON returns 0");

		// Missing nested fields returns 0.
		int missingResult = (int)Reflect.Static(typeof(ProtocolHelpers), "ParseRateLimitSecondsFromBody", types, ["{\"error\":{}}"])!;
		ctx.AssertEqual(0, missingResult, "ParseRateLimitSecondsFromBody: missing fields returns 0");
	}

	private static void TestTryAdaptToError(TestContext ctx)
	{
		ProtocolChatCompletions protocol = new ProtocolChatCompletions();
		Type[] types = new Type[] { typeof(HttpResponseMessage), typeof(string), typeof(bool) };

		// reasoningConfigured is false throughout: these cases exercise the parallel-tool-calls adaptation,
		// not the reasoning-hint fallback.
		// 400 with parallel_tool_calls triggers adaptation
		HttpResponseMessage r400 = new HttpResponseMessage(HttpStatusCode.BadRequest);
		string parallelBody = "{\"error\": \"parallel_tool_calls not supported\"}";
		bool adapted = (bool)Reflect.Instance(protocol, "TryAdaptToError", types, new object[] { r400, parallelBody, false })!;
		ctx.Assert(adapted, "TryAdaptToError: disables parallel_tool_calls on 400");

		// Subsequent call with same error returns false (already disabled)
		bool second = (bool)Reflect.Instance(protocol, "TryAdaptToError", types, new object[] { r400, parallelBody, false })!;
		ctx.Assert(!second, "TryAdaptToError: already disabled returns false");

		// 400 with upstream_error triggers adaptation
		ProtocolChatCompletions fresh = new ProtocolChatCompletions();
		string upstreamBody = "{\"error\": \"upstream_error occurred\"}";
		bool upstream = (bool)Reflect.Instance(fresh, "TryAdaptToError", types, new object[] { r400, upstreamBody, false })!;
		ctx.Assert(upstream, "TryAdaptToError: disables parallel_tool_calls on upstream_error");

		// 500 returns false
		HttpResponseMessage r500 = new HttpResponseMessage(HttpStatusCode.InternalServerError);
		bool serverErr = (bool)Reflect.Instance(fresh, "TryAdaptToError", types, new object[] { r500, upstreamBody, false })!;
		ctx.Assert(!serverErr, "TryAdaptToError: 500 status returns false");

		// 429 returns false
		HttpResponseMessage r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
		bool rateLimit = (bool)Reflect.Instance(fresh, "TryAdaptToError", types, new object[] { r429, upstreamBody, false })!;
		ctx.Assert(!rateLimit, "TryAdaptToError: 429 status returns false");

		// 400 without parallel_tool_calls or upstream_error returns false
		bool noMatch = (bool)Reflect.Instance(fresh, "TryAdaptToError", types, new object[] { r400, "{\"error\": \"other\"}", false })!;
		ctx.Assert(!noMatch, "TryAdaptToError: unrelated 400 returns false");
	}
}