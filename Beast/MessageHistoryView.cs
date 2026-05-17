using Terminal.Gui;


// Full-height scrollable message history.
// Rules:
//   - Each message is either expanded (full text) or collapsed (single summary line).
//   - Collapse defaults are driven by ConversationModel.Mode (Verbose/Minimized/Quiet).
//   - Click anywhere on a message to toggle its collapsed state individually.
//   - If the scroll bar is at the bottom, new content auto-scrolls to follow.
//   - If the user has scrolled up, the top visible line is anchored in place across
//     content changes and terminal resize events.
public class MessageHistoryView : View
{
    private readonly ConversationModel _model;

    // Each rendered row maps back to its source message index for click handling.
    private readonly List<int> _rowToMessageIndex = new List<int>();
    private List<string> _rows = new List<string>();

    private int _scrollOffset = 0;     // first visible row index
    private bool _anchoredAtBottom = true;

    // When not anchored at bottom, this is the text of the first visible row we try to preserve.
    private string _anchorText = "";

    private int _totalRows = 0;

    public MessageHistoryView(ConversationModel model)
    {
        _model = model;
        CanFocus = false;
        model.MessageUpdated += OnMessageUpdated;
    }

    // Called on every ConversationModel.Update() event (on any thread).
    private void OnMessageUpdated(DisplayMessage _)
    {
        Application.MainLoop.Invoke(Rebuild);
    }

    // Rebuild row map, apply anchoring, then redraw.
    private void Rebuild()
    {
        int visibleHeight = Frame.Height;
        if (visibleHeight <= 0) visibleHeight = 1;
        int visibleWidth = Math.Max(Frame.Width, 20);

        // Remember which text was at the top before the rebuild.
        string previousAnchor = _anchorText;

        _rowToMessageIndex.Clear();
        _rows = BuildRows(visibleWidth);
        _totalRows = _rows.Count;

        if (_anchoredAtBottom)
        {
            _scrollOffset = Math.Max(0, _totalRows - visibleHeight);
        }
        else
        {
            // Find the first row that still starts with previousAnchor.
            int found = -1;
            for (int r = 0; r < _rows.Count; r++)
            {
                if (_rows[r].StartsWith(previousAnchor, StringComparison.Ordinal))
                {
                    found = r;
                    break;
                }
            }
            if (found >= 0)
            {
                _scrollOffset = found;
            }
            else
            {
                _scrollOffset = Math.Max(0, _totalRows - visibleHeight);
            }
        }

        _scrollOffset = Clamp(_scrollOffset, 0, Math.Max(0, _totalRows - visibleHeight));
        UpdateAnchorText(_rows);
        SetNeedsDisplay();
    }

    public override void Redraw(Rect bounds)
    {
        Clear();

        int visibleWidth = bounds.Width;
        int visibleHeight = bounds.Height;

        for (int row = 0; row < visibleHeight; row++)
        {
            int dataRow = _scrollOffset + row;
            Move(0, row);
            if (dataRow < _rows.Count)
            {
                string line = _rows[dataRow];
                Driver.AddStr(line.PadRight(visibleWidth));
            }
            else
            {
                Driver.AddStr("".PadRight(visibleWidth));
            }
        }
    }

    // Builds the flat list of rendered rows from all messages and fills _rowToMessageIndex.
    private List<string> BuildRows(int width)
    {
        _rowToMessageIndex.Clear();
        List<string> rows = new List<string>();
        IReadOnlyList<DisplayMessage> messages = _model.Messages;

        foreach (DisplayMessage msg in messages)
        {
            string header = BuildHeader(msg);
            if (msg.Collapsed)
            {
                rows.Add(header);
                _rowToMessageIndex.Add(msg.Index);
            }
            else
            {
                rows.Add(header);
                _rowToMessageIndex.Add(msg.Index);

                // Wrap content lines.
                List<string> wrapped = WrapText(msg.Content, width - 2);
                foreach (string line in wrapped)
                {
                    rows.Add("  " + line);
                    _rowToMessageIndex.Add(msg.Index);
                }

                // Blank separator after expanded message.
                rows.Add("");
                _rowToMessageIndex.Add(msg.Index);
            }
        }

        return rows;
    }

    private static string BuildHeader(DisplayMessage msg)
    {
        string prefix = msg.Type switch
        {
            FrameType.Output    => "▸ ",
            FrameType.System    => "⚙ ",
            FrameType.Thinking  => msg.Collapsed ? "▶ [thinking] " : "▼ [thinking] ",
            FrameType.Tool      => msg.Collapsed ? "▶ [tool] " : "▼ [tool] ",
            FrameType.Error     => "✗ ",
            _                   => "  "
        };

        if (msg.Collapsed)
        {
            // Show first 60 chars of content as summary.
            string summary = msg.Content.Length > 60
                ? msg.Content.Substring(0, 57) + "..."
                : msg.Content;
            // Strip newlines from the summary.
            summary = summary.Replace('\n', ' ').Replace('\r', ' ');
            return prefix + summary;
        }

        return prefix;
    }

    // Wraps text at word boundaries up to maxWidth.
    private static List<string> WrapText(string text, int maxWidth)
    {
        List<string> result = new List<string>();
        if (maxWidth < 1) maxWidth = 1;

        string[] lines = text.Split('\n');
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                result.Add("");
                continue;
            }

            int pos = 0;
            while (pos < line.Length)
            {
                int remaining = line.Length - pos;
                if (remaining <= maxWidth)
                {
                    result.Add(line.Substring(pos));
                    pos = line.Length;
                }
                else
                {
                    // Try to break at last space within maxWidth.
                    int breakAt = maxWidth;
                    int lastSpace = line.LastIndexOf(' ', pos + maxWidth - 1, maxWidth);
                    if (lastSpace > pos) breakAt = lastSpace - pos;
                    result.Add(line.Substring(pos, breakAt));
                    pos += breakAt;
                    // Skip the space we broke on.
                    if (pos < line.Length && line[pos] == ' ') pos++;
                }
            }
        }

        return result;
    }

    public override bool OnMouseEvent(MouseEvent me)
    {
        if (me.Flags.HasFlag(MouseFlags.Button1Clicked))
        {
            int clickedRow = _scrollOffset + me.Y;
            if (clickedRow >= 0 && clickedRow < _rowToMessageIndex.Count)
            {
                int msgIndex = _rowToMessageIndex[clickedRow];
                _model.ToggleCollapsed(msgIndex);
            }
            return true;
        }

        if (me.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            Scroll(3);
            return true;
        }

        if (me.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            Scroll(-3);
            return true;
        }

        return base.OnMouseEvent(me);
    }

    // Scroll by delta rows, then update anchoring state.
    private void Scroll(int delta)
    {
        int visibleHeight = Math.Max(Frame.Height, 1);
        int maxOffset = Math.Max(0, _totalRows - visibleHeight);
        _scrollOffset = Clamp(_scrollOffset + delta, 0, maxOffset);
        _anchoredAtBottom = _scrollOffset >= maxOffset;

        List<string> rows = BuildRows(Math.Max(Frame.Width, 20));
        UpdateAnchorText(rows);
        SetNeedsDisplay();
    }

    private void UpdateAnchorText(List<string> rows)
    {
        if (_scrollOffset >= 0 && _scrollOffset < rows.Count)
        {
            string line = rows[_scrollOffset];
            // Store enough chars to uniquely identify the row.
            _anchorText = line.Length > 30 ? line.Substring(0, 30) : line;
        }
        else
        {
            _anchorText = "";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _model.MessageUpdated -= OnMessageUpdated;
        }
        base.Dispose(disposing);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
