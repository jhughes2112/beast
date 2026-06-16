using System.Collections.Generic;
using System.Text.Json.Nodes;


// Unit tests for ProtocolProxy protocol switching. Verifies that switching between
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
	// rehydrated from canonical — verifying the proxy correctly resets the slot.
	private static void TestSameProtocolSwitch(TestContext ctx)
	{
		ctx.Log("    SameProtocolSwitch");

		List<CanonicalMessage> messages = new List<CanonicalMessage>();
		ListenerBundle bundle = new ListenerBundle(new CanonicalConversation(messages), null);

		bundle.OnUserMessage("hello");
		bundle.OnAssistantTurn("world", "", new List<SemanticToolCall>());

		ctx.AssertEqual(2, messages.Count, "SameProtocol: canonical has 2 messages");

		ProtocolProxy proxy = BuildTestProxy();
		ProtocolChatCompletions first = proxy.EnsureProtocolChatCompletions(messages);
		int firstCount = NativeCount(first, "_native");
		ctx.AssertEqual(2, firstCount, "SameProtocol: first instance rehydrated from canonical");

		proxy.Invalidate();
		ProtocolChatCompletions second = proxy.EnsureProtocolChatCompletions(messages);

		ctx.Assert(!ReferenceEquals(first, second), "SameProtocol: invalidate yields new instance");
		ctx.AssertEqual(2, NativeCount(second, "_native"), "SameProtocol: new instance rehydrated with same history");
		ctx.AssertEqual(2, messages.Count, "SameProtocol: canonical unchanged through switch");
	}

	// Switching from ChatCompletions to Anthropic rehydrates Anthropic from canonical
	// and nulls the ChatCompletions slot. Switching back creates a new ChatCompletions
	// instance containing the full accumulated history.
	private static void TestCrossProtocolSwitch(TestContext ctx)
	{
		ctx.Log("    CrossProtocolSwitch");

		List<CanonicalMessage> messages = new List<CanonicalMessage>();
		ListenerBundle bundle = new ListenerBundle(new CanonicalConversation(messages), null);

		bundle.OnUserMessage("first");
		bundle.OnAssistantTurn("reply1", "", new List<SemanticToolCall>());
		int countAfterTurn1 = messages.Count;  // 2

		ProtocolProxy proxy = BuildTestProxy();
		ProtocolChatCompletions cc1 = proxy.EnsureProtocolChatCompletions(messages);
		ctx.AssertEqual(countAfterTurn1, NativeCount(cc1, "_native"), "CrossProtocol: ChatCompletions rehydrated");

		// Switch to Anthropic — cc1 slot should be nulled, Anthropic rehydrated.
		ProtocolAnthropic anthropic = proxy.EnsureProtocolAnthropic(messages);
		ctx.Assert(NativeCount(anthropic, "_native") > 0, "CrossProtocol: Anthropic rehydrated from canonical");

		// Events after the switch accumulate into canonical.
		bundle.OnUserMessage("second");
		bundle.OnAssistantTurn("reply2", "", new List<SemanticToolCall>());
		int countAfterTurn2 = messages.Count;

		ctx.Assert(countAfterTurn2 > countAfterTurn1, "CrossProtocol: canonical grew after protocol switch");

		// Switch back to ChatCompletions — must be a new instance with the full history.
		ProtocolChatCompletions cc2 = proxy.EnsureProtocolChatCompletions(messages);
		ctx.Assert(!ReferenceEquals(cc1, cc2), "CrossProtocol: switch back yields new CC instance");
		ctx.AssertEqual(countAfterTurn2, NativeCount(cc2, "_native"), "CrossProtocol: new CC rehydrated with full history");
	}

	private static ProtocolProxy BuildTestProxy()
	{
		LlmModel model = new LlmModel("test", "http://localhost", string.Empty, new List<JsonObject>(), new List<JsonObject>(), new ModelConfig());
		return new ProtocolProxy(model);
	}

	// Returns the Count of the named collection field via reflection.
	// Works for both JsonArray (ICollection<T> only) and List<T> (ICollection + ICollection<T>).
	private static int NativeCount(object target, string fieldName)
	{
		object native = Reflect.GetField(target, fieldName)!;
		return (int)native.GetType().GetProperty("Count")!.GetValue(native)!;
	}
}
