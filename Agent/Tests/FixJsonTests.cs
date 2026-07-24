using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;


public static class FixJsonTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  FixJsonTests");

		TestMarkdownStripping(ctx);
		TestStructuralRepair(ctx);
		TestNonStandardSyntax(ctx);
		TestFuzzyToolName(ctx);
		TestTypeCoercions(ctx);
		TestArgNameReconciliation(ctx);
		TestLooseBooleans(ctx);
		TestExtraArgsStripped(ctx);
		TestMissingRequiredArgs(ctx);
		TestFullPipeline(ctx);
		TestXmlToolCallExtraction(ctx);
	}

	// ─── <tool_call> text salvage (template-mismatched local models) ─────────

	private static void TestXmlToolCallExtraction(TestContext ctx)
	{
		List<ToolDefinition> offered = new List<ToolDefinition>
		{
			new ToolDefinition { Function = new FunctionDefinition { Name = "read_file" } },
			new ToolDefinition { Function = new FunctionDefinition { Name = "ls" } }
		};
		Type[] signature = new[] { typeof(string), typeof(List<ToolDefinition>), typeof(List<SemanticToolCall>) };

		// A well-formed literal <tool_call> block naming an offered tool becomes a real call.
		List<SemanticToolCall> calls = new List<SemanticToolCall>();
		string text = "I will read the file now.\n<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"file_path\": \"a.cs\"}}\n</tool_call>";
		string cleaned = (string)Reflect.Static(typeof(ProtocolChatCompletions), "ExtractXmlToolCalls",
			signature, new object[] { text, offered, calls })!;
		ctx.AssertEqual(1, calls.Count, "XmlToolCall: extracted one call");
		ctx.AssertEqual("read_file", calls.Count > 0 ? calls[0].Name : "", "XmlToolCall: name recovered");
		if (calls.Count > 0)
			ctx.AssertContains(calls[0].ArgumentsJson, "a.cs", "XmlToolCall: arguments recovered");
		ctx.AssertEqual("I will read the file now.", cleaned, "XmlToolCall: block stripped, prose preserved");

		// The Qwen-Coder function dialect: <function=name> with <parameter=key> blocks whose raw
		// multi-line values must survive verbatim (minus the template's framing newlines).
		List<SemanticToolCall> qwen = new List<SemanticToolCall>();
		string qwenText = "Listing now.\n<tool_call>  <function=ls>\n<parameter=folder>\n/workspace/Design\n</parameter>\n</function></tool_call>";
		string qwenCleaned = (string)Reflect.Static(typeof(ProtocolChatCompletions), "ExtractXmlToolCalls",
			signature, new object[] { qwenText, offered, qwen })!;
		ctx.AssertEqual(1, qwen.Count, "XmlToolCall: qwen function form extracted");
		ctx.AssertEqual("ls", qwen.Count > 0 ? qwen[0].Name : "", "XmlToolCall: qwen function name");
		if (qwen.Count > 0)
			ctx.AssertContains(qwen[0].ArgumentsJson, "/workspace/Design", "XmlToolCall: qwen parameter value");
		ctx.AssertEqual("Listing now.", qwenCleaned, "XmlToolCall: qwen block stripped");

		// A well-formed block naming a tool that is NOT offered this turn stays as prose — no
		// call, no error; a quoted example or hallucinated tool must never round-trip to dispatch.
		List<SemanticToolCall> unknown = new List<SemanticToolCall>();
		string unknownText = "<tool_call>{\"name\": \"rm_rf_everything\", \"arguments\": {}}</tool_call>";
		string unknownKept = (string)Reflect.Static(typeof(ProtocolChatCompletions), "ExtractXmlToolCalls",
			signature, new object[] { unknownText, offered, unknown })!;
		ctx.AssertEqual(0, unknown.Count, "XmlToolCall: unknown tool produces no call");
		ctx.AssertContains(unknownKept, "rm_rf_everything", "XmlToolCall: unknown-tool block kept as text");

		// A malformed block produces no call and stays visible in the text.
		List<SemanticToolCall> none = new List<SemanticToolCall>();
		string kept = (string)Reflect.Static(typeof(ProtocolChatCompletions), "ExtractXmlToolCalls",
			signature, new object[] { "<tool_call>not json</tool_call>", offered, none })!;
		ctx.AssertEqual(0, none.Count, "XmlToolCall: malformed block produces no call");
		ctx.AssertContains(kept, "<tool_call>", "XmlToolCall: malformed block kept visible");
	}

	// ─── Argument-name reconciliation ────────────────────────────────────────

	private static void TestArgNameReconciliation(TestContext ctx)
	{
		FunctionDefinition schema = Def("edit_file",
			Props(("file_path", "string"), ("old_text", "string"), ("new_text", "string")),
			"file_path", "old_text", "new_text");

		// Wrong casing on a required arg is mapped to the canonical name, not stripped then failed.
		(JsonObject? r1, string? e1) = FixJson.TryParseWithSchema(
			"{\"File_Path\": \"/f\", \"OLD_TEXT\": \"a\", \"new_text\": \"b\"}", schema, null);
		ctx.AssertNotNull(r1, "Arg: case-insensitive names parse");
		ctx.AssertNull(e1, "Arg: case-insensitive names no error");
		ctx.AssertEqual("/f", r1?["file_path"]?.GetValue<string>(), "Arg: File_Path → file_path");
		ctx.AssertEqual("a", r1?["old_text"]?.GetValue<string>(), "Arg: OLD_TEXT → old_text");

		// A near-miss (camelCase / dropped underscore) is fuzzy-mapped to the canonical name.
		(JsonObject? r2, string? e2) = FixJson.TryParseWithSchema(
			"{\"file_path\": \"/f\", \"oldText\": \"a\", \"newtext\": \"b\"}", schema, null);
		ctx.AssertNotNull(r2, "Arg: fuzzy names parse");
		ctx.AssertNull(e2, "Arg: fuzzy names no error");
		ctx.AssertEqual("a", r2?["old_text"]?.GetValue<string>(), "Arg: oldText → old_text");
		ctx.AssertEqual("b", r2?["new_text"]?.GetValue<string>(), "Arg: newtext → new_text");

		// A genuinely unrelated key is NOT mapped — it stays extra (and is stripped), not mis-assigned.
		FunctionDefinition single = Def("t", Props(("file_path", "string")), "file_path");
		(JsonObject? r3, string? e3) = FixJson.TryParseWithSchema(
			"{\"file_path\": \"/f\", \"thoughts\": \"...\"}", single, null);
		ctx.AssertNotNull(r3, "Arg: unrelated key parses");
		ctx.AssertNull(e3, "Arg: unrelated key no error");
		ctx.Assert(r3 != null && !r3.ContainsKey("thoughts"), "Arg: unrelated key not mapped, stripped");

		// An exact canonical key already present is never clobbered by a near-miss sibling.
		(JsonObject? r4, string? e4) = FixJson.TryParseWithSchema(
			"{\"file_path\": \"/f\", \"old_text\": \"keep\", \"oldText\": \"drop\", \"new_text\": \"b\"}", schema, null);
		ctx.AssertNotNull(r4, "Arg: canonical-present parses");
		ctx.AssertEqual("keep", r4?["old_text"]?.GetValue<string>(), "Arg: existing canonical value preserved");
	}

	// ─── Loose boolean coercion ──────────────────────────────────────────────

	private static void TestLooseBooleans(TestContext ctx)
	{
		FunctionDefinition schema = Def("finish_review", Props(("approved", "boolean")), "approved");

		(JsonObject? r1, string? _) = FixJson.TryParseWithSchema("{\"approved\": \"yes\"}", schema, null);
		ctx.AssertEqual(true, r1?["approved"]?.GetValue<bool>(), "Bool: 'yes' → true");

		(JsonObject? r2, string? _) = FixJson.TryParseWithSchema("{\"approved\": \"no\"}", schema, null);
		ctx.AssertEqual(false, r2?["approved"]?.GetValue<bool>(), "Bool: 'no' → false");

		(JsonObject? r3, string? _) = FixJson.TryParseWithSchema("{\"approved\": 1}", schema, null);
		ctx.AssertEqual(true, r3?["approved"]?.GetValue<bool>(), "Bool: 1 → true");

		(JsonObject? r4, string? _) = FixJson.TryParseWithSchema("{\"approved\": \"true\"}", schema, null);
		ctx.AssertEqual(true, r4?["approved"]?.GetValue<bool>(), "Bool: 'true' → true");
	}

	// ─── Stage 1: Markdown / prose stripping ─────────────────────────────────

	private static void TestMarkdownStripping(TestContext ctx)
	{
		// Code fence with language tag
		JsonObject? r1 = FixJson.TryParseObject("```json\n{\"k\": \"v\"}\n```");
		ctx.AssertNotNull(r1, "Mark: json-fenced parses");
		ctx.AssertEqual("v", r1?["k"]?.GetValue<string>(), "Mark: json-fenced value");

		// Code fence without language tag
		JsonObject? r2 = FixJson.TryParseObject("```\n{\"k\": \"v\"}\n```");
		ctx.AssertNotNull(r2, "Mark: bare-fenced parses");
		ctx.AssertEqual("v", r2?["k"]?.GetValue<string>(), "Mark: bare-fenced value");

		// Prose prefix before the JSON object
		JsonObject? r3 = FixJson.TryParseObject("Here are the arguments: {\"k\": \"v\"}");
		ctx.AssertNotNull(r3, "Mark: prose prefix parses");
		ctx.AssertEqual("v", r3?["k"]?.GetValue<string>(), "Mark: prose prefix value");

		// Already clean — no change needed
		JsonObject? r4 = FixJson.TryParseObject("{\"k\": \"v\"}");
		ctx.AssertNotNull(r4, "Mark: clean JSON passes through");
		ctx.AssertEqual("v", r4?["k"]?.GetValue<string>(), "Mark: clean JSON value");
	}

	// ─── Stage 2: Structural repair ──────────────────────────────────────────

	private static void TestStructuralRepair(TestContext ctx)
	{
		// The original bug report: missing closing brace
		string? r1 = FixJson.Repair("{\"file_path\": \"/workspace/MEMORY.md\"", null);
		ctx.AssertNotNull(r1, "Repair: missing } non-null");
		ctx.AssertEqual("{\"file_path\": \"/workspace/MEMORY.md\"}", r1, "Repair: missing } exact result");

		// Trailing comma before truncation
		string? r2 = FixJson.Repair("{\"k\": \"v\",", null);
		ctx.AssertNotNull(r2, "Repair: trailing comma non-null");
		JsonObject? o2 = TryParse(r2!);
		ctx.AssertNotNull(o2, "Repair: trailing comma result parses");
		ctx.AssertEqual("v", o2?["k"]?.GetValue<string>(), "Repair: trailing comma value preserved");

		// Truncated mid-string value
		string? r3 = FixJson.Repair("{\"k\": \"truncated", null);
		ctx.AssertNotNull(r3, "Repair: truncated string non-null");
		JsonObject? o3 = TryParse(r3!);
		ctx.AssertNotNull(o3, "Repair: truncated string result parses");
		ctx.AssertContains(o3?["k"]?.GetValue<string>() ?? "", "truncated", "Repair: truncated string partial value");

		// Trailing key + colon — dangling key stripped, preceding value preserved
		string? r4 = FixJson.Repair("{\"k1\": \"v1\", \"k2\":", null);
		ctx.AssertNotNull(r4, "Repair: dangling colon non-null");
		JsonObject? o4 = TryParse(r4!);
		ctx.AssertNotNull(o4, "Repair: dangling colon result parses");
		ctx.AssertEqual("v1", o4?["k1"]?.GetValue<string>(), "Repair: dangling colon keeps prior value");
		ctx.Assert(o4 != null && !o4.ContainsKey("k2"), "Repair: dangling colon drops incomplete key");

		// Nested structure truncated: array missing close bracket then object missing brace
		string? r5 = FixJson.Repair("{\"arr\": [1, 2, 3", null);
		ctx.AssertNotNull(r5, "Repair: nested truncated non-null");
		JsonObject? o5 = TryParse(r5!);
		ctx.AssertNotNull(o5, "Repair: nested truncated parses");
		ctx.Assert(o5?["arr"] is JsonArray, "Repair: nested truncated array type preserved");

		// Complete JSON — nothing to repair, returns null
		string? r6 = FixJson.Repair("{\"k\": \"v\"}", null);
		ctx.AssertNull(r6, "Repair: complete JSON returns null");

		// Mismatched brackets — unfixable, returns null
		string? r7 = FixJson.Repair("{\"k\": \"v\"]}", null);
		ctx.AssertNull(r7, "Repair: mismatched brackets returns null");

		// Bare open brace
		string? r8 = FixJson.Repair("{", null);
		ctx.AssertNotNull(r8, "Repair: bare { non-null");
		ctx.AssertEqual("{}", r8, "Repair: bare { closes to {}");

		// Value truncated with a second key that is itself truncated
		string? r9 = FixJson.Repair("{\"a\": \"hello\", \"b\": \"wor", null);
		ctx.AssertNotNull(r9, "Repair: second value truncated non-null");
		JsonObject? o9 = TryParse(r9!);
		ctx.AssertNotNull(o9, "Repair: second value truncated parses");
		ctx.AssertEqual("hello", o9?["a"]?.GetValue<string>(), "Repair: first value intact");
	}

	// ─── Stage 1 (non-standard): single quotes and unquoted keys ─────────────

	private static void TestNonStandardSyntax(TestContext ctx)
	{
		// Single-quoted strings
		JsonObject? r1 = FixJson.TryParseObject("{'key': 'value'}");
		ctx.AssertNotNull(r1, "NonStd: single-quoted parses");
		ctx.AssertEqual("value", r1?["key"]?.GetValue<string>(), "NonStd: single-quoted value");

		// Unquoted object key
		JsonObject? r2 = FixJson.TryParseObject("{key: \"value\"}");
		ctx.AssertNotNull(r2, "NonStd: unquoted key parses");
		ctx.AssertEqual("value", r2?["key"]?.GetValue<string>(), "NonStd: unquoted key value");

		// Both combined
		JsonObject? r3 = FixJson.TryParseObject("{key: 'value'}");
		ctx.AssertNotNull(r3, "NonStd: unquoted key + single-quoted value parses");
		ctx.AssertEqual("value", r3?["key"]?.GetValue<string>(), "NonStd: combo value");

		// Single-quoted string containing a double quote (must be escaped in output)
		JsonObject? r4 = FixJson.TryParseObject("{'key': 'say \"hello\"'}");
		ctx.AssertNotNull(r4, "NonStd: single-quoted with embedded double-quote parses");
		ctx.AssertEqual("say \"hello\"", r4?["key"]?.GetValue<string>(), "NonStd: embedded double-quote value");

		// Multiple fields with single quotes
		JsonObject? r5 = FixJson.TryParseObject("{'file_path': '/foo/bar', 'mode': 'read'}");
		ctx.AssertNotNull(r5, "NonStd: multi-field single-quoted parses");
		ctx.AssertEqual("/foo/bar", r5?["file_path"]?.GetValue<string>(), "NonStd: multi-field first value");
		ctx.AssertEqual("read", r5?["mode"]?.GetValue<string>(), "NonStd: multi-field second value");

		// Multiple unquoted keys
		JsonObject? r6 = FixJson.TryParseObject("{file_path: \"/foo\", mode: \"read\"}");
		ctx.AssertNotNull(r6, "NonStd: multi unquoted keys parse");
		ctx.AssertEqual("/foo", r6?["file_path"]?.GetValue<string>(), "NonStd: multi unquoted first value");
		ctx.AssertEqual("read", r6?["mode"]?.GetValue<string>(), "NonStd: multi unquoted second value");
	}

	// ─── Stage 3: Fuzzy tool name matching ───────────────────────────────────

	private static void TestFuzzyToolName(TestContext ctx)
	{
		string[] tools = new string[] { "read_file", "write_file", "edit_file", "bash", "fetch_url" };

		// camelCase → snake_case (distance 1: remove underscore)
		string? r1 = FixJson.FuzzyMatchToolName("editFile", tools, 3, null);
		ctx.AssertEqual("edit_file", r1, "Fuzzy: editFile → edit_file");

		// Hyphen → underscore (distance 1)
		string? r2 = FixJson.FuzzyMatchToolName("read-file", tools, 3, null);
		ctx.AssertEqual("read_file", r2, "Fuzzy: read-file → read_file");

		// Extra trailing 's' (distance 1)
		string? r3 = FixJson.FuzzyMatchToolName("read_files", tools, 3, null);
		ctx.AssertEqual("read_file", r3, "Fuzzy: read_files → read_file");

		// Doubled letter typo (distance 1)
		string? r4 = FixJson.FuzzyMatchToolName("baash", tools, 3, null);
		ctx.AssertEqual("bash", r4, "Fuzzy: baash → bash");

		// writeFile camelCase (distance 1)
		string? r5 = FixJson.FuzzyMatchToolName("writeFile", tools, 3, null);
		ctx.AssertEqual("write_file", r5, "Fuzzy: writeFile → write_file");

		// Too far — returns null
		string? r6 = FixJson.FuzzyMatchToolName("completely_wrong_name", tools, 3, null);
		ctx.AssertNull(r6, "Fuzzy: far miss returns null");

		// Exact match — returns null (ordinally identical; caller already did exact match)
		string? r7 = FixJson.FuzzyMatchToolName("bash", tools, 3, null);
		ctx.AssertNull(r7, "Fuzzy: exact match returns null");

		// Case-only mismatch (distance 0 case-insensitively) — corrects the casing since the caller's
		// exact match was case-sensitive and failed.
		string? r7b = FixJson.FuzzyMatchToolName("Bash", tools, 3, null);
		ctx.AssertEqual("bash", r7b, "Fuzzy: Bash → bash (case correction)");

		// Empty input — all distances > threshold
		string? r8 = FixJson.FuzzyMatchToolName("", tools, 3, null);
		ctx.AssertNull(r8, "Fuzzy: empty input returns null");

		// Empty tool list
		string? r9 = FixJson.FuzzyMatchToolName("edit_file", new string[0], 3, null);
		ctx.AssertNull(r9, "Fuzzy: empty tool list returns null");
	}

	// ─── Stage 4: Type coercions ─────────────────────────────────────────────

	private static void TestTypeCoercions(TestContext ctx)
	{
		FunctionDefinition intDef = Def("t", Props(("count", "integer")));

		// String "3" → integer 3
		(JsonObject? r1, string? e1) = FixJson.TryParseWithSchema("{\"count\": \"3\"}", intDef, null);
		ctx.AssertNotNull(r1, "Coerce: str→int parses");
		ctx.AssertNull(e1, "Coerce: str→int no error");
		ctx.AssertEqual(3, r1?["count"]?.GetValue<int>(), "Coerce: str→int value");

		// String "3.14" → number 3.14
		FunctionDefinition numDef = Def("t", Props(("value", "number")));
		(JsonObject? r2, string? e2) = FixJson.TryParseWithSchema("{\"value\": \"3.14\"}", numDef, null);
		ctx.AssertNotNull(r2, "Coerce: str→number parses");
		ctx.AssertNull(e2, "Coerce: str→number no error");
		double? dbl = r2?["value"]?.GetValue<double>();
		ctx.Assert(dbl.HasValue && Math.Abs(dbl.Value - 3.14) < 0.001, "Coerce: str→number value");

		// String "true" → boolean true
		FunctionDefinition boolDef = Def("t", Props(("flag", "boolean")));
		(JsonObject? r3, string? e3) = FixJson.TryParseWithSchema("{\"flag\": \"true\"}", boolDef, null);
		ctx.AssertNotNull(r3, "Coerce: str→bool parses");
		ctx.AssertNull(e3, "Coerce: str→bool no error");
		ctx.AssertEqual(true, r3?["flag"]?.GetValue<bool>(), "Coerce: str→bool value");

		// String "false" → boolean false
		(JsonObject? r3b, string? e3b) = FixJson.TryParseWithSchema("{\"flag\": \"false\"}", boolDef, null);
		ctx.AssertNotNull(r3b, "Coerce: str→false parses");
		ctx.AssertEqual(false, r3b?["flag"]?.GetValue<bool>(), "Coerce: str→false value");

		// Integer 42 → string "42"
		FunctionDefinition strDef = Def("t", Props(("name", "string")));
		(JsonObject? r4, string? e4) = FixJson.TryParseWithSchema("{\"name\": 42}", strDef, null);
		ctx.AssertNotNull(r4, "Coerce: int→str parses");
		ctx.AssertNull(e4, "Coerce: int→str no error");
		ctx.AssertEqual("42", r4?["name"]?.GetValue<string>(), "Coerce: int→str value");

		// Already correct type — no coercion, value unchanged
		(JsonObject? r5, string? e5) = FixJson.TryParseWithSchema("{\"count\": 3}", intDef, null);
		ctx.AssertNotNull(r5, "Coerce: correct type passes");
		ctx.AssertNull(e5, "Coerce: correct type no error");
		ctx.AssertEqual(3, r5?["count"]?.GetValue<int>(), "Coerce: correct type value unchanged");

		// Non-coercible value — stays as-is, no error (coercion skipped gracefully)
		(JsonObject? r6, string? e6) = FixJson.TryParseWithSchema("{\"count\": \"notanumber\"}", intDef, null);
		ctx.AssertNotNull(r6, "Coerce: bad value still parses");
		ctx.AssertNull(e6, "Coerce: bad value no error");
		ctx.AssertEqual("notanumber", r6?["count"]?.GetValue<string>(), "Coerce: bad value unchanged");
	}

	// ─── Stage 6: Extra / hallucinated arg stripping ─────────────────────────

	private static void TestExtraArgsStripped(TestContext ctx)
	{
		FunctionDefinition schema = Def("t", Props(("file_path", "string")));

		// One hallucinated arg
		(JsonObject? r1, string? e1) = FixJson.TryParseWithSchema(
			"{\"file_path\": \"/foo\", \"thoughts\": \"I should...\"}",
			schema, null);
		ctx.AssertNotNull(r1, "Extra: one hallucinated parses");
		ctx.AssertNull(e1, "Extra: one hallucinated no error");
		ctx.AssertEqual("/foo", r1?["file_path"]?.GetValue<string>(), "Extra: valid arg kept");
		ctx.Assert(r1 != null && !r1.ContainsKey("thoughts"), "Extra: hallucinated arg stripped");

		// Multiple hallucinated args
		(JsonObject? r2, string? e2) = FixJson.TryParseWithSchema(
			"{\"file_path\": \"/foo\", \"thinking\": \"...\", \"reasoning\": \"...\"}",
			schema, null);
		ctx.AssertNotNull(r2, "Extra: multi hallucinated parses");
		ctx.AssertNull(e2, "Extra: multi hallucinated no error");
		ctx.Assert(r2 != null && !r2.ContainsKey("thinking"), "Extra: thinking stripped");
		ctx.Assert(r2 != null && !r2.ContainsKey("reasoning"), "Extra: reasoning stripped");
		ctx.Assert(r2 != null && r2.ContainsKey("file_path"), "Extra: file_path kept after multi-strip");

		// No extra args — object unchanged
		(JsonObject? r3, string? e3) = FixJson.TryParseWithSchema("{\"file_path\": \"/foo\"}", schema, null);
		ctx.AssertNotNull(r3, "Extra: none present parses");
		ctx.AssertNull(e3, "Extra: none present no error");
		ctx.Assert(r3 != null && CountKeys(r3) == 1, "Extra: key count unchanged when no extras");
	}

	// ─── Stage 5: Missing required args ──────────────────────────────────────

	private static void TestMissingRequiredArgs(TestContext ctx)
	{
		FunctionDefinition schema = Def("edit_file",
			Props(("file_path", "string"), ("old_text", "string"), ("new_text", "string")),
			"file_path", "old_text", "new_text");

		// All required present — success
		(JsonObject? r1, string? e1) = FixJson.TryParseWithSchema(
			"{\"file_path\": \"/f\", \"old_text\": \"a\", \"new_text\": \"b\"}",
			schema, null);
		ctx.AssertNotNull(r1, "Missing: all present parses");
		ctx.AssertNull(e1, "Missing: all present no error");

		// One required arg absent — hard error
		(JsonObject? r2, string? e2) = FixJson.TryParseWithSchema(
			"{\"file_path\": \"/f\", \"old_text\": \"a\"}",
			schema, null);
		ctx.AssertNull(r2, "Missing: new_text absent: null result");
		ctx.AssertNotNull(e2, "Missing: new_text absent: error returned");
		ctx.AssertContains(e2!, "new_text", "Missing: error names the missing field");

		// All required absent
		(JsonObject? r3, string? e3) = FixJson.TryParseWithSchema("{}", schema, null);
		ctx.AssertNull(r3, "Missing: all absent: null result");
		ctx.AssertNotNull(e3, "Missing: all absent: error returned");

		// Schema with no required array — empty object is fine
		FunctionDefinition optSchema = Def("t", Props(("file_path", "string")));
		(JsonObject? r4, string? e4) = FixJson.TryParseWithSchema("{}", optSchema, null);
		ctx.AssertNotNull(r4, "Missing: no required array passes empty object");
		ctx.AssertNull(e4, "Missing: no required array no error");
	}

	// ─── Full pipeline: compound scenarios ───────────────────────────────────

	private static void TestFullPipeline(TestContext ctx)
	{
		FunctionDefinition readSchema = Def("read_file",
			Props(("file_path", "string"), ("offset", "string"), ("lines", "string")),
			"file_path");

		// Original bug report: truncated JSON missing closing brace
		(JsonObject? r1, string? e1) = FixJson.TryParseWithSchema(
			"{\"file_path\": \"/workspace/MEMORY.md\"", readSchema, null);
		ctx.AssertNotNull(r1, "Full: original bug parses");
		ctx.AssertNull(e1, "Full: original bug no error");
		ctx.AssertEqual("/workspace/MEMORY.md", r1?["file_path"]?.GetValue<string>(), "Full: original bug value");

		// Markdown fence + truncated
		(JsonObject? r2, string? e2) = FixJson.TryParseWithSchema(
			"```json\n{\"file_path\": \"/workspace/MEMORY.md\"", readSchema, null);
		ctx.AssertNotNull(r2, "Full: fenced+truncated parses");
		ctx.AssertNull(e2, "Full: fenced+truncated no error");
		ctx.AssertEqual("/workspace/MEMORY.md", r2?["file_path"]?.GetValue<string>(), "Full: fenced+truncated value");

		// Single quotes + truncated
		(JsonObject? r3, string? e3) = FixJson.TryParseWithSchema(
			"{'file_path': '/workspace/MEMORY.md'", readSchema, null);
		ctx.AssertNotNull(r3, "Full: single-quoted+truncated parses");
		ctx.AssertNull(e3, "Full: single-quoted+truncated no error");
		ctx.AssertEqual("/workspace/MEMORY.md", r3?["file_path"]?.GetValue<string>(), "Full: single-quoted+truncated value");

		// Prose prefix + valid JSON
		(JsonObject? r4, string? e4) = FixJson.TryParseWithSchema(
			"Here are the args: {\"file_path\": \"/foo\"}", readSchema, null);
		ctx.AssertNotNull(r4, "Full: prose prefix parses");
		ctx.AssertNull(e4, "Full: prose prefix no error");
		ctx.AssertEqual("/foo", r4?["file_path"]?.GetValue<string>(), "Full: prose prefix value");

		// Type coerce + extra strip together
		FunctionDefinition bashSchema = Def("bash",
			Props(("command", "string"), ("timeout_seconds", "integer")),
			"command");
		(JsonObject? r5, string? e5) = FixJson.TryParseWithSchema(
			"{\"command\": \"ls\", \"timeout_seconds\": \"30\", \"thoughts\": \"should run ls\"}",
			bashSchema, null);
		ctx.AssertNotNull(r5, "Full: coerce+strip parses");
		ctx.AssertNull(e5, "Full: coerce+strip no error");
		ctx.AssertEqual("ls", r5?["command"]?.GetValue<string>(), "Full: coerce+strip command");
		ctx.AssertEqual(30, r5?["timeout_seconds"]?.GetValue<int>(), "Full: coerce+strip timeout coerced");
		ctx.Assert(r5 != null && !r5.ContainsKey("thoughts"), "Full: coerce+strip extras stripped");

		// Missing required after repair — hard error, not silently swallowed
		(JsonObject? r6, string? e6) = FixJson.TryParseWithSchema("{}", readSchema, null);
		ctx.AssertNull(r6, "Full: missing required after repair: null result");
		ctx.AssertNotNull(e6, "Full: missing required after repair: error returned");
		ctx.AssertContains(e6!, "file_path", "Full: error names the missing field");

		// Empty input
		(JsonObject? r7, string? e7) = FixJson.TryParseWithSchema("", readSchema, null);
		ctx.AssertNull(r7, "Full: empty input: null result");
		ctx.AssertNotNull(e7, "Full: empty input: error returned");

		// Unquoted key + type coerce
		(JsonObject? r8, string? e8) = FixJson.TryParseWithSchema(
			"{command: 'ls -la', timeout_seconds: '60'}", bashSchema, null);
		ctx.AssertNotNull(r8, "Full: unquoted+single-quote+coerce parses");
		ctx.AssertNull(e8, "Full: unquoted+single-quote+coerce no error");
		ctx.AssertEqual("ls -la", r8?["command"]?.GetValue<string>(), "Full: command value");
		ctx.AssertEqual(60, r8?["timeout_seconds"]?.GetValue<int>(), "Full: timeout coerced from string");
	}

	// ─── Helpers ─────────────────────────────────────────────────────────────

	private static JsonObject? TryParse(string json)
	{
		try
		{
			return JsonNode.Parse(json)?.AsObject();
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static int CountKeys(JsonObject obj)
	{
		int count = 0;
		foreach ((string _, JsonNode? _) in obj)
			count++;
		return count;
	}

	private static JsonObject Prop(string type)
		=> new JsonObject { ["type"] = type };

	private static JsonObject Props(params (string key, string type)[] fields)
	{
		JsonObject props = new JsonObject();
		foreach ((string key, string type) in fields)
			props[key] = Prop(type);
		return props;
	}

	private static FunctionDefinition Def(string name, JsonObject props, params string[] required)
	{
		JsonObject parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = props
		};

		if (required.Length > 0)
		{
			JsonArray req = new JsonArray();
			foreach (string r in required)
				req.Add((JsonNode)r);
			parameters["required"] = req;
		}

		return new FunctionDefinition { Name = name, Description = name, Parameters = parameters };
	}
}