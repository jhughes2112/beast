using System.Collections.Generic;

// Composes a set of IProtocolListener peers and forwards each call to all peers except the
// sender. Implements IProtocolListener itself so producers can talk to a single object and
// not care about the topology. Bundles are session-scoped — the session owns one.
public class ListenerBundle : IProtocolListener
{
    private readonly List<IProtocolListener> _listeners = new List<IProtocolListener>();

    public void Add(IProtocolListener listener)
    {
        _listeners.Add(listener);
    }

    // Returns the first listener of the requested concrete type, or null.
    public T? Get<T>() where T : class, IProtocolListener
    {
        foreach (IProtocolListener l in _listeners)
        {
            if (l is T typed) return typed;
        }
        return null;
    }

    public void OnSystemMessage(IProtocolListener sender, string text)
    {
        foreach (IProtocolListener l in _listeners)
        {
            if (!ReferenceEquals(l, sender)) l.OnSystemMessage(sender, text);
        }
    }

    public void OnUserMessage(IProtocolListener sender, string text)
    {
        foreach (IProtocolListener l in _listeners)
        {
            if (!ReferenceEquals(l, sender)) l.OnUserMessage(sender, text);
        }
    }

    public void OnAssistantTurn(IProtocolListener sender, string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
    {
        foreach (IProtocolListener l in _listeners)
        {
            if (!ReferenceEquals(l, sender)) l.OnAssistantTurn(sender, text, thinking, toolCalls);
        }
    }

    public void OnToolResult(IProtocolListener sender, string toolCallId, string content)
    {
        foreach (IProtocolListener l in _listeners)
        {
            if (!ReferenceEquals(l, sender)) l.OnToolResult(sender, toolCallId, content);
        }
    }

    public void OnStreamStart(IProtocolListener sender, string tag)
    {
        foreach (IProtocolListener l in _listeners)
        {
            if (!ReferenceEquals(l, sender)) l.OnStreamStart(sender, tag);
        }
    }

    public void OnStreamChunk(IProtocolListener sender, string tag, string chunk)
    {
        foreach (IProtocolListener l in _listeners)
        {
            if (!ReferenceEquals(l, sender)) l.OnStreamChunk(sender, tag, chunk);
        }
    }

    public void OnStreamEnd(IProtocolListener sender, string tag)
    {
        foreach (IProtocolListener l in _listeners)
        {
            if (!ReferenceEquals(l, sender)) l.OnStreamEnd(sender, tag);
        }
    }

    public void OnClear()
    {
        foreach (IProtocolListener l in _listeners)
        {
            l.OnClear();
        }
    }

    public string? GetLastAssistantText()
    {
        string? text = null;
        foreach (IProtocolListener l in _listeners)
        {
            text = l.GetLastAssistantText();
            if (!string.IsNullOrWhiteSpace(text)) break;
        }
        return text;
    }

    }
