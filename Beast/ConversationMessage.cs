using System.Collections.Generic;


// Controls the initial collapse state applied to messages as they arrive.
// Individual messages can always be toggled regardless of mode.
public enum CollapseMode
{
    Verbose,    // nothing collapsed on arrival
    Minimized,  // Tool, Thinking, and System collapsed; Output and Error expanded
    Quiet       // everything collapsed on arrival
}

// A single slot in the conversation display. Index is stable; content is overwritten in place for streaming.
public class DisplayMessage
{
    public int Index { get; }
    public FrameType Type { get; set; }
    public string Content { get; set; }
    public bool Collapsed { get; set; }

    public DisplayMessage(int index, FrameType type, string content)
    {
        Index = index;
        Type = type;
        Content = content;
        Collapsed = false;
    }
}

// Holds all display messages and notifies the UI of changes.
// Update() is the only mutation path — callers always supply (index, type, content).
public class ConversationModel
{
    private readonly List<DisplayMessage> _messages = new List<DisplayMessage>();
    private CollapseMode _mode = CollapseMode.Minimized;

    // Fired on every Update() or ToggleCollapsed() call. Argument is the affected message.
    public event System.Action<DisplayMessage>? MessageUpdated;

    public IReadOnlyList<DisplayMessage> Messages => _messages;

    public CollapseMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            // Re-apply collapse state to every existing message and notify.
            foreach (DisplayMessage msg in _messages)
            {
                msg.Collapsed = ShouldCollapse(msg.Type, _mode);
                MessageUpdated?.Invoke(msg);
            }
        }
    }

    // Creates or overwrites the message at index with the given type and content.
    // New slots get the mode-derived default collapse state.
    // Existing slots preserve the user's current collapsed state when only content changes;
    // if the type changes the mode default is re-applied.
    public void Update(int index, FrameType type, string content)
    {
        bool isNew = _messages.Count <= index;

        while (_messages.Count <= index)
        {
            _messages.Add(new DisplayMessage(_messages.Count, FrameType.Output, ""));
        }

        DisplayMessage msg = _messages[index];
        bool typeChanged = msg.Type != type;
        msg.Type = type;
        msg.Content = content;

        if (isNew || typeChanged)
        {
            msg.Collapsed = ShouldCollapse(type, _mode);
        }

        MessageUpdated?.Invoke(msg);
    }

    public void ToggleCollapsed(int index)
    {
        if (index < 0 || index >= _messages.Count) return;
        DisplayMessage msg = _messages[index];
        msg.Collapsed = !msg.Collapsed;
        MessageUpdated?.Invoke(msg);
    }

    public void Clear()
    {
        _messages.Clear();
        MessageUpdated?.Invoke(new DisplayMessage(0, FrameType.Clear, string.Empty));
    }

    // Returns whether a message of the given type should be hidden entirely in the given mode.
    public static bool ShouldHide(FrameType type, CollapseMode mode)
    {
        if (mode == CollapseMode.Verbose) return false;

        // Debug and ToolCall are always hidden in non-verbose modes.
        if (type == FrameType.Debug || type == FrameType.ToolCall) return true;

        // Quiet: only Output and Error are visible.
        if (mode == CollapseMode.Quiet)
            return type != FrameType.Output && type != FrameType.Error;

        return false;
    }

    private static bool ShouldCollapse(FrameType type, CollapseMode mode)
    {
        if (mode == CollapseMode.Verbose) return false;

        // Minimized: Tool/ToolResponse/Thinking/System shown collapsed; Output/Error expanded.
        if (mode == CollapseMode.Minimized)
            return type == FrameType.Thinking || type == FrameType.Tool || type == FrameType.ToolResponse || type == FrameType.System;

        // Quiet: everything collapsed (hidden types are filtered separately by ShouldHide).
        return true;
    }
}
