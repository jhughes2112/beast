using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;


public static class LlmServiceTests
{
	public static void Test(TestContext ctx)
	{
		Console.WriteLine("  LlmServiceTests");

		LlmService service = BuildTestService();
		List<Tool> tools = BuildTestTools();

		TestEpochToSecondsFromNow(ctx);
		TestGetFirstHeaderValue(ctx);
		TestXmlToolCallParsing(ctx, service, tools);
		TestIsRateLimited(ctx, service);
		TestParseRateLimitSecondsFromErrorBody(ctx, service);
		TestTryAdaptToError(ctx);
	}

	private static LlmService BuildTestService()
	{
		ModelConfig config = new ModelConfig
		{
			Id = "test-model-id",
			Name = "test-model",
			ContextWindow = 128000,
			Cost = new CostConfig { Input = 0m, Output = 0m }
		};
		LlmModel model = new LlmModel(config.Id, "https://test.example.com/v1", "test-key", new System.Collections.Generic.Dictionary<string, string>(), config);
		LlmService svc = new LlmService(model);
		return svc;
	}

	private static List<Tool> BuildTestTools()
	{
		List<Tool> tools = new List<Tool>();

		tools.Add(new Tool
		{
			Definition = new ToolDefinition
			{
				Type = "function",
				Function = new FunctionDefinition
				{
					Name = "read_file",
					Description = "Read a file",
					Parameters = new JsonObject
					{
						["type"] = "object",
						["properties"] = new JsonObject
						{
							["path"] = new JsonObject { ["type"] = "string" },
							["encoding"] = new JsonObject { ["type"] = "string" }
						}
					}
				}
			},
			Handler = (JsonObject args, CancellationToken ct2) => Task.FromResult(new ToolResult("file content", false))
		});

		tools.Add(new Tool
		{
			Definition = new ToolDefinition
			{
				Type = "function",
				Function = new FunctionDefinition
				{
					Name = "write_file",
					Description = "Write a file",
					Parameters = new JsonObject
					{
						["type"] = "object",
						["properties"] = new JsonObject
						{
							["path"] = new JsonObject { ["type"] = "string" },
							["content"] = new JsonObject { ["type"] = "string" }
						}
					}
				}
			},
			Handler = (JsonObject args, CancellationToken ct2) => Task.FromResult(new ToolResult("ok", false))
		});

		return tools;
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

	private static (List<ConversationToolCall> calls, List<string> errors) ParseXml(LlmService service, string content, Tool[] toolArray)
	{
		Type[] types = [typeof(string), typeof(Tool[])];
		object result = Reflect.Instance(service, "TryParseXmlToolCalls", types, [content, toolArray])!;
		// Unpack the ValueTuple<List<ConversationToolCall>, List<string>>
		List<ConversationToolCall> calls = (List<ConversationToolCall>)result.GetType().GetField("Item1")!.GetValue(result)!;
		List<string> errors = (List<string>)result.GetType().GetField("Item2")!.GetValue(result)!;
		return (calls, errors);
	}

	private static void TestXmlToolCallParsing(TestContext ctx, LlmService service, List<Tool> tools)
	{
		Tool[] toolArray = new Tool[tools.Count];
		for (int i = 0; i < tools.Count; i++) toolArray[i] = tools[i];

		// Valid single tool_call tag.
		string validContent = "Some text\n<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"/test.txt\"}}\n</tool_call>";
		(List<ConversationToolCall> valid, List<string> _) = ParseXml(service, validContent, toolArray);
		ctx.AssertEqual(1, valid.Count, "XmlToolCalls: single call found");
		ctx.AssertEqual("read_file", valid[0].Function.Name, "XmlToolCalls: correct tool name");

		// function_call variant.
		string fcContent = "<function_call>{\"name\": \"write_file\", \"arguments\": {\"path\": \"a.txt\", \"content\": \"hello\"}}</function_call>";
		(List<ConversationToolCall> fc, List<string> _) = ParseXml(service, fcContent, toolArray);
		ctx.AssertEqual(1, fc.Count, "XmlToolCalls: function_call variant parsed");
		ctx.AssertEqual("write_file", fc[0].Function.Name, "XmlToolCalls: function_call correct name");

		// Multiple tool calls in one response.
		string multiContent = "<tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a\"}}</tool_call>\n<tool_call>{\"name\": \"write_file\", \"arguments\": {\"path\": \"b\", \"content\": \"c\"}}</tool_call>";
		(List<ConversationToolCall> multi, List<string> _) = ParseXml(service, multiContent, toolArray);
		ctx.AssertEqual(2, multi.Count, "XmlToolCalls: multiple calls found");

		// No tags returns empty lists.
		(List<ConversationToolCall> noTagsCalls, List<string> noTagsErrors) = ParseXml(service, "Just a regular response", toolArray);
		ctx.AssertEqual(0, noTagsCalls.Count, "XmlToolCalls: no tags returns no calls");
		ctx.AssertEqual(0, noTagsErrors.Count, "XmlToolCalls: no tags returns no errors");

		// Unknown tool name yields an error.
		string unknownTool = "<tool_call>{\"name\": \"unknown_tool\", \"arguments\": {}}</tool_call>";
		(List<ConversationToolCall> unknownCalls, List<string> unknownErrors) = ParseXml(service, unknownTool, toolArray);
		ctx.AssertEqual(0, unknownCalls.Count, "XmlToolCalls: unknown tool yields no calls");
		ctx.Assert(unknownErrors.Count > 0, "XmlToolCalls: unknown tool yields error");

		// Extra argument not in definition yields an error.
		string extraArg = "<tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a\", \"bogus\": \"b\"}}</tool_call>";
		(List<ConversationToolCall> extraCalls, List<string> extraErrors) = ParseXml(service, extraArg, toolArray);
		ctx.AssertEqual(0, extraCalls.Count, "XmlToolCalls: extra arg yields no calls");
		ctx.Assert(extraErrors.Count > 0, "XmlToolCalls: extra arg yields error");

		// Invalid JSON inside tags yields an error.
		string invalidJson = "<tool_call>not valid json at all</tool_call>";
		(List<ConversationToolCall> invalidCalls, List<string> invalidErrors) = ParseXml(service, invalidJson, toolArray);
		ctx.AssertEqual(0, invalidCalls.Count, "XmlToolCalls: invalid JSON yields no calls");
		ctx.Assert(invalidErrors.Count > 0, "XmlToolCalls: invalid JSON yields error");

		// Empty arguments is valid.
		string emptyArgs = "<tool_call>{\"name\": \"read_file\", \"arguments\": {}}</tool_call>";
		(List<ConversationToolCall> emptyCalls, List<string> _) = ParseXml(service, emptyArgs, toolArray);
		ctx.AssertEqual(1, emptyCalls.Count, "XmlToolCalls: empty args is valid");

		// parameters key accepted as alias for arguments.
		string paramsKey = "<tool_call>{\"name\": \"read_file\", \"parameters\": {\"path\": \"test\"}}</tool_call>";
		(List<ConversationToolCall> paramsCalls, List<string> _) = ParseXml(service, paramsKey, toolArray);
		ctx.AssertEqual(1, paramsCalls.Count, "XmlToolCalls: parameters key accepted");

		// Case-insensitive tags.
		string upperCase = "<TOOL_CALL>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a\"}}</TOOL_CALL>";
		(List<ConversationToolCall> upperCalls, List<string> _) = ParseXml(service, upperCase, toolArray);
		ctx.AssertEqual(1, upperCalls.Count, "XmlToolCalls: case insensitive tags");

		// Missing name field yields an error.
		string noName = "<tool_call>{\"arguments\": {\"path\": \"a\"}}</tool_call>";
		(List<ConversationToolCall> noNameCalls, List<string> noNameErrors) = ParseXml(service, noName, toolArray);
		ctx.AssertEqual(0, noNameCalls.Count, "XmlToolCalls: missing name yields no calls");
		ctx.Assert(noNameErrors.Count > 0, "XmlToolCalls: missing name yields error");

		// Generated IDs start with xmltc_ prefix.
		ctx.Assert(valid[0].Id.StartsWith("xmltc_"), "XmlToolCalls: generated ID has xmltc_ prefix");
	}

	private static void TestIsRateLimited(TestContext ctx, LlmService service)
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

	private static void TestParseRateLimitSecondsFromErrorBody(TestContext ctx, LlmService service)
	{
		Type[] types = [typeof(string)];

		// Valid nested error body with X-RateLimit-Reset.
		long futureEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
		string validBody = $"{{\"error\":{{\"metadata\":{{\"headers\":{{\"X-RateLimit-Reset\":\"{futureEpoch}\"}}}}}}}}";
		int validResult = (int)Reflect.Instance(service, "ParseRateLimitSecondsFromErrorBody", types, [validBody])!;
		ctx.Assert(validResult >= 59 && validResult <= 62, "ParseRateLimitSecondsFromErrorBody: valid body parsed");

		// Invalid JSON returns 0.
		int invalidResult = (int)Reflect.Instance(service, "ParseRateLimitSecondsFromErrorBody", types, ["not json at all"])!;
		ctx.AssertEqual(0, invalidResult, "ParseRateLimitSecondsFromErrorBody: invalid JSON returns 0");

		// Missing nested fields returns 0.
		int missingResult = (int)Reflect.Instance(service, "ParseRateLimitSecondsFromErrorBody", types, ["{\"error\":{}}"])!;
		ctx.AssertEqual(0, missingResult, "ParseRateLimitSecondsFromErrorBody: missing fields returns 0");
	}

	private static void TestTryAdaptToError(TestContext ctx)
	{
		LlmService service = BuildTestService();
		Type[] types = new Type[] { typeof(HttpResponseMessage), typeof(string) };

		// 400 with parallel_tool_calls triggers adaptation
		HttpResponseMessage r400 = new HttpResponseMessage(HttpStatusCode.BadRequest);
		string parallelBody = "{\"error\": \"parallel_tool_calls not supported\"}";
		bool adapted = (bool)Reflect.Instance(service, "TryAdaptToError", types, new object[] { r400, parallelBody })!;
		ctx.Assert(adapted, "TryAdaptToError: disables parallel_tool_calls on 400");

		// Subsequent call with same error returns false (already disabled)
		bool second = (bool)Reflect.Instance(service, "TryAdaptToError", types, new object[] { r400, parallelBody })!;
		ctx.Assert(!second, "TryAdaptToError: already disabled returns false");

		// 400 with upstream_error triggers adaptation
		LlmService fresh = BuildTestService();
		string upstreamBody = "{\"error\": \"upstream_error occurred\"}";
		bool upstream = (bool)Reflect.Instance(fresh, "TryAdaptToError", types, new object[] { r400, upstreamBody })!;
		ctx.Assert(upstream, "TryAdaptToError: disables parallel_tool_calls on upstream_error");

		// 500 returns false
		HttpResponseMessage r500 = new HttpResponseMessage(HttpStatusCode.InternalServerError);
		bool serverErr = (bool)Reflect.Instance(fresh, "TryAdaptToError", types, new object[] { r500, upstreamBody })!;
		ctx.Assert(!serverErr, "TryAdaptToError: 500 status returns false");

		// 429 returns false
		HttpResponseMessage r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
		bool rateLimit = (bool)Reflect.Instance(fresh, "TryAdaptToError", types, new object[] { r429, upstreamBody })!;
		ctx.Assert(!rateLimit, "TryAdaptToError: 429 status returns false");

		// 400 without parallel_tool_calls or upstream_error returns false
		bool noMatch = (bool)Reflect.Instance(fresh, "TryAdaptToError", types, new object[] { r400, "{\"error\": \"other\"}" })!;
		ctx.Assert(!noMatch, "TryAdaptToError: unrelated 400 returns false");
	}
}
