using System.Collections.Generic;


// Canonical conversation store. Wraps the typed message list owned by BeastSession and
// provides semantic mutation methods. The list reference is shared, so mutations here
// are immediately visible in BeastSession for persistence.
//
// Streaming events are no-ops — canonical records only completed turns. The producing
// protocol already handles streaming display via transport.
public class CanonicalConversation
{
    private readonly List<CanonicalMessage> _messages;

    public CanonicalConversation(List<CanonicalMessage> messages)
    {
        _messages = messages;
    }

    public IReadOnlyList<CanonicalMessage> Messages => _messages;

    public void OnSystemMessage(string text)
    {
        // Update an existing system message at the head rather than inserting a duplicate.
        if (_messages.Count > 0 && _messages[0] is SystemMessage)
        {
            _messages[0] = new SystemMessage(text);
            return;
        }
        _messages.Insert(0, new SystemMessage(text));
    }

    public void OnUserMessage(string text)
    {
        // Merge into a trailing user message to collapse rapid multi-line input into one turn.
        if (_messages.Count > 0 && _messages[_messages.Count - 1] is UserMessage last)
        {
            _messages[_messages.Count - 1] = new UserMessage(last.Text + "\n" + text);
            return;
        }
        _messages.Add(new UserMessage(text));
    }

    public void OnAssistantTurn(string text, string thinking, IReadOnlyList<SemanticToolCall> toolCalls)
    {
        _messages.Add(new AssistantMessage(text, thinking, toolCalls));
    }

    public void OnToolResult(string toolCallId, ToolResult result)
    {
        string content = result.StdOut;
        if (!string.IsNullOrEmpty(result.StdErr))
            content = content + "\nstderr: " + result.StdErr;
        _messages.Add(new ToolResultMessage(toolCallId, content));
    }

    public void OnStreamStart(string tag) { }
    public void OnStreamChunk(string tag, string chunk) { }
    public void OnStreamEnd(string tag) { }

    public void OnClear()
    {
        _messages.Clear();
    }
}
