using System.Collections.Generic;
using System.Text.Json.Nodes;

// Holds the listeners for one session and fans every semantic call out to all populated
// listeners except the sender. The bundle keeps an explicit nullable slot per protocol type
// and ensures exactly one is active at a time. When the proxy detects which protocol an
// endpoint needs, it calls the corresponding EnsureProtocol* method; the bundle creates the
// instance if absent (or the wrong type is active), calls Rehydrate, and nulls the others.
public class ListenerBundle : IProtocolListener
{
    // Canonical persisted conversation store. Always present for the life of the bundle.
    private readonly ListenerChatCompletions _canonical;

    // Renders semantic events to the connected client. Null when no client is attached (e.g. internal tool calls).
    private readonly ListenerTransport? _transport;

    // Exactly one of these is non-null at any time once the first model executes.
    private ProtocolChatCompletions? _protocolChatCompletions;
    private ProtocolResponses?       _protocolResponses;
    private ProtocolAnthropic?       _protocolAnthropic;

    public ListenerBundle(ListenerChatCompletions canonical, ListenerTransport? transport)
    {
        _canonical = canonical;
        _transport = transport;
    }

    public ListenerChatCompletions Canonical => _canonical;
    public ListenerTransport? Transport => _transport;

    // Returns the active ChatCompletions protocol, creating and rehydrating it if needed.
    // Nulls the other protocol slots on a switch.
    public ProtocolChatCompletions EnsureProtocolChatCompletions()
    {
        if (_protocolChatCompletions == null)
        {
            _protocolChatCompletions = new ProtocolChatCompletions();
            _protocolChatCompletions.Rehydrate(_canonical.State);
            _protocolResponses = null;
            _protocolAnthropic = null;
        }
        return _protocolChatCompletions;
    }

    // Returns the active Responses protocol, creating and rehydrating it if needed.
    // Nulls the other protocol slots on a switch.
    public ProtocolResponses EnsureProtocolResponses()
    {
        if (_protocolResponses == null)
        {
            _protocolResponses = new ProtocolResponses();
            _protocolResponses.Rehydrate(_canonical.State);
            _protocolChatCompletions = null;
            _protocolAnthropic = null;
        }
        return _protocolResponses;
    }

    // Returns the active Anthropic protocol, creating and rehydrating it if needed.
    // Nulls the other protocol slots on a switch.
    public ProtocolAnthropic EnsureProtocolAnthropic()
    {
        if (_protocolAnthropic == null)
        {
            _protocolAnthropic = new ProtocolAnthropic();
            _protocolAnthropic.Rehydrate(_canonical.State);
            _protocolChatCompletions = null;
            _protocolResponses = null;
        }
        return _protocolAnthropic;
    }

    private IProtocolListener? ActiveProtocol
    {
        get
        {
            if (_protocolChatCompletions != null) return _protocolChatCompletions;
            if (_protocolResponses != null) return _protocolResponses;
            return _protocolAnthropic;
        }
    }

    // Forwards a call to every populated listener except the sender.
    private void Each(IProtocolListener sender, System.Action<IProtocolListener> action)
    {
        if (!ReferenceEquals(_canonical, sender)) action(_canonical);

        IProtocolListener? protocol = ActiveProtocol;
        if (protocol != null && !ReferenceEquals(protocol, sender)) action(protocol);

        if (_transport != null && !ReferenceEquals(_transport, sender)) action(_transport);
    }

    public void OnSystemMessage(IProtocolListener sender, string text)
    {
        Each(sender, l => l.OnSystemMessage(sender, text));
    }

    public void OnUserMessage(IProtocolListener sender, string text)
    {
        Each(sender, l => l.OnUserMessage(sender, text));
    }

    public void OnAssistantTurn(IProtocolListener sender, string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
    {
        Each(sender, l => l.OnAssistantTurn(sender, text, thinking, toolCalls));
    }

    public void OnToolResult(IProtocolListener sender, string toolCallId, ToolResult result)
    {
        Each(sender, l => l.OnToolResult(sender, toolCallId, result));
    }

    public void OnStreamStart(IProtocolListener sender, string tag)
    {
        Each(sender, l => l.OnStreamStart(sender, tag));
    }

    public void OnStreamChunk(IProtocolListener sender, string tag, string chunk)
    {
        Each(sender, l => l.OnStreamChunk(sender, tag, chunk));
    }

    public void OnStreamEnd(IProtocolListener sender, string tag)
    {
        Each(sender, l => l.OnStreamEnd(sender, tag));
    }

    public void OnClear()
    {
        // Clear reaches every populated listener including the sender-less caller.
        _canonical.OnClear();

        IProtocolListener? protocol = ActiveProtocol;
        if (protocol != null) protocol.OnClear();

        if (_transport != null) _transport.OnClear();
    }

    public void Rehydrate(JsonArray canonical) { }

    // Drops the active protocol instance so the next EnsureProtocol* call creates a fresh one
    // and rehydrates from canonical. Call whenever the model changes so the new protocol starts
    // clean rather than replaying stale per-endpoint state (e.g. old _previousResponseId).
    public void InvalidateProtocol()
    {
        _protocolChatCompletions = null;
        _protocolResponses = null;
        _protocolAnthropic = null;
    }

    // Returns the most recent assistant text from the canonical conversation, or null.
    // Canonical is the single source of truth; protocol-native listeners are not consulted.
    public string? GetLastAssistantText()
    {
        JsonArray state = _canonical.State;

        string? text = null;
        for (int i = state.Count - 1; i >= 0; i--)
        {
            JsonNode? n = state[i];
            if (n != null && n["role"]?.GetValue<string>() == "assistant")
            {
                text = n["content"]?.GetValue<string>();
                break;
            }
        }

        return text;
    }
}
