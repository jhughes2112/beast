using System;
using System.Collections.Generic;


// The rendered view of one markdown message (User, Output, Thinking) — the only way those messages render,
// whether the text arrives whole or a token at a time. A whole message is one push; a stream is many.
//
// The rows are kept as a stack of rendered markdown blocks. Markdown can rewrite the block it is still inside
// (a "---" turns the paragraph above into a heading, a delimiter row turns it into a table, a fence swallows
// everything after it), but once a later block has begun the earlier ones are closed. So each push pops the
// open block at the top, re-parses its text with the new text, and pushes the result back: every block
// Markdig now reports before the last is closed and stays put. A push costs the open block's length, not the
// message's. Rows live in one Screen that is resized in place; closed rows are written into it once.
internal sealed class MarkdownBlock
{
	public int Slot { get; }

	private readonly FrameType    _type;
	private readonly int          _width;
	private readonly int          _wrapW;
	private readonly bool         _wordWrap;
	private readonly string       _indent;
	private readonly string       _prefix;
	private readonly Cell         _fill;
	private readonly List<string> _rows = new List<string>();
	private readonly Screen       _expanded;

	private readonly int    _generation;      // the message's generation this render follows
	private          int    _consumed;        // characters of the message pushed so far
	private          string _open       = ""; // source of the open block, tabs expanded
	private          string _openIndent = ""; // heading section indent the open block renders under
	private          int    _closedRows;      // rows of closed blocks; the open block's rows follow
	private          int    _closedBlocks;    // closed blocks so far, for the blank separator before the next
	private          int    _closedWidth;     // widest closed row
	private          Screen _collapsed;

	// A render of msg at width w, following its current generation; nothing is pushed until Build.
	public MarkdownBlock(DisplayMessage msg, int width)
	{
		Slot        = msg.Index;
		_type       = msg.Type;
		_generation = msg.Generation;
		_width      = width;
		_wrapW      = BlockRenderer.WrapWidth(_type, width);
		_wordWrap   = BlockRenderer.WordWraps(_type);
		_indent     = BlockRenderer.IndentsBlock(_type) ? "  " : "";
		_prefix     = BlockRenderer.PrefixTextForType(_type);
		_fill       = BlockRenderer.RowFill(_type);
		_expanded   = new Screen(width, 0, _fill);
		Push("");
		_collapsed = BuildCollapsed("");
	}

	// The markdown message types. The system prompt is plain instructional text and stays off this path.
	// Thinking renders as markdown unconditionally, exactly like assistant output — a content-sniffing gate
	// made formatting vanish whenever the full text scored differently than the partial stream. Newlines
	// survive: the renderer emits every LineBreakInline as a real line break.
	public static bool Handles(FrameType type)
	{
		return type == FrameType.User || type == FrameType.Output || type == FrameType.Thinking;
	}

	// Whether msg's content can be pushed onto this render: same slot, type and width, and a message that has
	// only been appended to since (its generation unchanged). Anything else (a resize, a rewritten message)
	// starts a fresh render.
	public bool Follows(DisplayMessage msg, int w)
	{
		return msg.Index == Slot && msg.Type == _type && w == _width && msg.Generation == _generation;
	}

	// Pushes whatever the message gained since the last call and returns its layer.
	public BlockLayer Build(DisplayMessage msg)
	{
		if (msg.Length > _consumed)
		{
			string added = msg.Since(_consumed);
			_consumed    = msg.Length;
			Push(BlockRenderer.ExpandTabs(added));
			_collapsed = BuildCollapsed(msg.Head(HeaderReadLength()));
		}
		return new BlockLayer(Slot, _collapsed, _expanded, isExpanded: !msg.Collapsed);
	}

	// Pops the open block, re-parses it with text appended, and pushes back every block that came out of
	// it — all but the last now closed — then brings the expanded Screen up to date.
	private void Push(string text)
	{
		int                 firstDirtyRow = _closedRows;
		string              open          = _open + text;
		List<RenderedBlock> blocks        = MarkdownAnsi.RenderTopLevelBlocks(open, _type, _wrapW, _openIndent);
		int                 openIndex     = OpenBlockIndex(blocks);

		_rows.RemoveRange(_closedRows, _rows.Count - _closedRows);

		List<RenderLine> closed = new List<RenderLine>();
		MarkdownAnsi.AppendBlocks(blocks, 0, openIndex, _closedBlocks > 0, closed);
		AppendRows(closed);
		_closedRows    = _rows.Count;
		_closedBlocks += openIndex;
		if (openIndex > 0)
		{
			_openIndent = blocks[openIndex - 1].SectionIndentAfter;
			_open       = open.Substring(LineStart(open, blocks[openIndex].Start));
		}
		else
		{
			_open = open;
		}

		List<RenderLine> openLines = new List<RenderLine>();
		MarkdownAnsi.AppendBlocks(blocks, openIndex, blocks.Count, _closedBlocks > 0, openLines);
		MarkdownAnsi.TrimTrailingBlank(openLines);
		AppendRows(openLines);
		// A message that renders nothing still shows one row, carrying its prefix.
		if (_rows.Count == 0)
			_rows.Add(_indent + _prefix);

		WriteRows(firstDirtyRow);
	}

	// The open block is the last one with a source position; any block Markdig appends after it without
	// one (a footnote group) stays open with it.
	private static int OpenBlockIndex(List<RenderedBlock> blocks)
	{
		int openIndex = 0;
		for (int i = 0; i < blocks.Count; i++)
		{
			if (blocks[i].Start >= 0)
				openIndex = i;
		}
		return openIndex;
	}

	// Restarting a parse mid-line would lose the line's leading indentation, which markdown reads, so the
	// open block is re-parsed from the start of the line it begins on.
	private static int LineStart(string text, int offset)
	{
		int start = offset;
		while (start > 0 && text[start - 1] != '\n')
			start--;
		return start;
	}

	// Wraps lines into display rows; the message's very first row carries its prefix.
	private void AppendRows(List<RenderLine> lines)
	{
		List<string> wrapped = new List<string>();
		BlockRenderer.AppendMarkdownRows(lines, _rows.Count == 0 ? _prefix : "", _wordWrap, _wrapW, wrapped);
		foreach (string row in wrapped)
			_rows.Add(_indent + row);
	}

	// Sizes the Screen to the rows — as wide as the widest row, never narrower than the viewport — and
	// rewrites the rows from firstDirtyRow down. Rows above it are closed and already written.
	private void WriteRows(int firstDirtyRow)
	{
		int openWidth = 0;
		for (int r = firstDirtyRow; r < _rows.Count; r++)
		{
			int len = AnsiString.VisibleLength(_rows[r]);
			if (r < _closedRows)
				_closedWidth = Math.Max(_closedWidth, len);
			else
				openWidth = Math.Max(openWidth, len);
		}

		int w = Math.Min(BlockRenderer.MaxBlockWidth, Math.Max(_width, Math.Max(_closedWidth, openWidth)));
		_expanded.Resize(w, _rows.Count, _fill);
		for (int r = firstDirtyRow; r < _rows.Count; r++)
			BlockRenderer.WriteBlockRow(_expanded, r, _rows[r], _type);
	}

	// The width the collapsed header has for its summary.
	private int HeaderWidth()
	{
		return Math.Max(1, _width - _prefix.Length - _indent.Length);
	}

	// How much of the text the header summary can use; more than this never shows.
	private int HeaderReadLength()
	{
		return HeaderWidth() * 2 + 2;
	}

	// The collapsed view: the header row alone, built from the head of the text only.
	private Screen BuildCollapsed(string head)
	{
		int    availW = HeaderWidth();
		string header = BlockRenderer.HeaderRow(_indent, _prefix, BlockRenderer.HeaderSummary(head, availW), availW, _rows.Count > 1);
		Screen s      = new Screen(_width, 1, _fill);
		BlockRenderer.WriteBlockRow(s, 0, header, _type);
		return s;
	}
}