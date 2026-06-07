using System.Collections.Generic;
using System.Collections;
using System.Text.Json.Nodes;


// Unit tests for ListenerBundle protocol switching. Verifies that switching between
// protocol types correctly creates a new instance and rehydrates it from canonical
// state, without making any real API calls.
public static class ProtocolSwitchTests
{
	public static void Test(TestContext ctx)
	{
		ctx.Log("  ProtocolSwitchTests");
		TestSameProtocolSwitch(ctx);
		TestCrossProtocolSwitch(ctx);
	}

	// Invalidating and re-ensuring the same protocol creates a fresh instance
	// rehydrated from canonical — verifying the bundle correctly resets the slot.
	private static void TestSameProtocolSwitch(TestContext ctx)
	{
		ctx.Log("    SameProtocolSwitch");

		JsonArray state = new JsonArray();
		ListenerBundle bundle = new ListenerBundle(new ListenerChatCompletions(state), null);

		bundle.OnUserMessage(null!, "hello");
		bundle.OnAssistantTurn(null!, "world", "", new List<SemanticToolCall>());

		ctx.AssertEqual(2, state.Count, "SameProtocol: canonical has 2 messages");

		ProtocolChatCompletions first = bundle.EnsureProtocolChatCompletions();
		int firstCount = NativeCount(first, "_native");
		ctx.AssertEqual(2, firstCount, "SameProtocol: first instance rehydrated from canonical");

		bundle.InvalidateProtocol();
		ProtocolChatCompletions second = bundle.EnsureProtocolChatCompletions();

		ctx.Assert(!ReferenceEquals(first, second), "SameProtocol: invalidate yields new instance");
		ctx.AssertEqual(2, NativeCount(second, "_native"), "SameProtocol: new instance rehydrated with same history");
		ctx.AssertEqual(2, state.Count, "SameProtocol: canonical unchanged through switch");
	}

	// Switching from ChatCompletions to Anthropic rehydrates Anthropic from canonical
	// and nulls the ChatCompletions slot. Switching back creates a new ChatCompletions
	// instance containing the full accumulated history.
	private static void TestCrossProtocolSwitch(TestContext ctx)
	{
		ctx.Log("    CrossProtocolSwitch");

		JsonArray state = new JsonArray();
		ListenerBundle bundle = new ListenerBundle(new ListenerChatCompletions(state), null);

		bundle.OnUserMessage(null!, "first");
		bundle.OnAssistantTurn(null!, "reply1", "", new List<SemanticToolCall>());
		int countAfterTurn1 = state.Count;  // 2

		ProtocolChatCompletions cc1 = bundle.EnsureProtocolChatCompletions();
		ctx.AssertEqual(countAfterTurn1, NativeCount(cc1, "_native"), "CrossProtocol: ChatCompletions rehydrated");

		// Switch to Anthropic — cc1 slot should be nulled, Anthropic rehydrated.
		ProtocolAnthropic anthropic = bundle.EnsureProtocolAnthropic();
		ctx.Assert(NativeCount(anthropic, "_native") > 0, "CrossProtocol: Anthropic rehydrated from canonical");

		// Events after the switch accumulate into canonical and the active Anthropic native state.
		bundle.OnUserMessage(null!, "second");
		bundle.OnAssistantTurn(null!, "reply2", "", new List<SemanticToolCall>());
		int countAfterTurn2 = state.Count;

		ctx.Assert(countAfterTurn2 > countAfterTurn1, "CrossProtocol: canonical grew after protocol switch");

		// Switch back to ChatCompletions — must be a new instance with the full history.
		ProtocolChatCompletions cc2 = bundle.EnsureProtocolChatCompletions();
		ctx.Assert(!ReferenceEquals(cc1, cc2), "CrossProtocol: switch back yields new CC instance");
		ctx.AssertEqual(countAfterTurn2, NativeCount(cc2, "_native"), "CrossProtocol: new CC rehydrated with full history");
	}

	// Returns the Count of the named ICollection field via reflection.
	private static int NativeCount(object target, string fieldName)
	{
		ICollection native = (ICollection)Reflect.GetField(target, fieldName)!;
		return native.Count;
	}
}
