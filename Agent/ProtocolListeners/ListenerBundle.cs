using System.Collections.Generic;

// Holds the listeners for one session and fans every semantic call out to canonical,
// the active protocol proxy, and transport. The proxy owns detection and protocol instance
// lifecycle; it registers itself on the bundle at the start of each execute call.
//
// Protocols that generate events (streaming, completed assistant turns) call bundle.Canonical
// and bundle.Transport directly to avoid routing back through the proxy.
public class ListenerBundle
{
	// Canonical persisted conversation store. Always present for the life of the bundle.
	private readonly CanonicalConversation _canonical;

	// Renders semantic events to the connected client. Null when no client is attached.
	private readonly ListenerTransport? _transport;

	// Set by ProtocolProxy.ExecuteAsync at the start of each turn. Null before the first
	// turn runs or after InvalidateProtocol() clears it.
	private ProtocolProxy? _activeProxy;

	public ListenerBundle(CanonicalConversation canonical, ListenerTransport? transport)
	{
		_canonical = canonical;
		_transport = transport;
	}

	public CanonicalConversation Canonical => _canonical;
	public ListenerTransport? Transport => _transport;

	// Called by ProtocolProxy at the start of ExecuteAsync so the bundle routes fan-out
	// events to the correct protocol instance for the duration of the session.
	internal void SetActiveProxy(ProtocolProxy proxy)
	{
		_activeProxy = proxy;
	}

	public void OnSystemMessage(string text)
	{
		_canonical.OnSystemMessage(text);
		_activeProxy?.OnSystemMessage(text);
		_transport?.OnSystemMessage(text);
	}

	public void OnUserMessage(string text)
	{
		_canonical.OnUserMessage(text);
		_activeProxy?.OnUserMessage(text);
		_transport?.OnUserMessage(text);
	}

	public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
	{
		_canonical.OnAssistantTurn(text, thinking, toolCalls);
		_activeProxy?.OnAssistantTurn(text, thinking, toolCalls);
		_transport?.OnAssistantTurn(text, thinking, toolCalls);
	}

	public void OnToolResult(ToolResult result)
	{
		_canonical.OnToolResult(result);
		_activeProxy?.OnToolResult(result);
		_transport?.OnToolResult(result);
	}

	// Resets the active proxy so the next turn re-probes and rehydrates from canonical.
	// Call whenever the model changes so the new protocol starts clean.
	public void InvalidateProtocol()
	{
		_activeProxy?.Invalidate();
		_activeProxy = null;
	}

	// Returns the most recent assistant text from the canonical conversation, or null.
	public string? GetLastAssistantText()
	{
		IReadOnlyList<CanonicalMessage> messages = _canonical.Messages;
		for (int i = messages.Count - 1; i >= 0; i--)
		{
			if (messages[i] is AssistantMessage am)
				return am.Text;
		}
		return null;
	}
}