using System;
using System.Collections.Generic;


// Represents one conversation block. Holds its slot index plus two pre-rendered Screens for the collapsed
// (one-line summary) and expanded (full content) views. Layout switches between them based on IsExpanded.
// Construct once per (block, terminal-width); rebuild on resize or content change.
public sealed class BlockLayer
{
    public int    SlotIndex  { get; }
    public Screen Collapsed  { get; }
    public Screen Expanded   { get; }
    public bool   IsExpanded { get; }

    public BlockLayer(int slotIndex, Screen collapsed, Screen expanded, bool isExpanded)
    {
        SlotIndex  = slotIndex;
        Collapsed  = collapsed;
        Expanded   = expanded;
        IsExpanded = isExpanded;
    }

    public Screen Current { get { return IsExpanded ? Expanded : Collapsed; } }
    public int    Height  { get { return Current.H; } }
}

// Where a block was placed on the composed long Screen.
public readonly struct BlockPlacement
{
    public readonly int SlotIndex;
    public readonly int Top;     // top row in the composite Screen, inclusive
    public readonly int Height;  // number of rows

    public BlockPlacement(int slotIndex, int top, int height)
    {
        SlotIndex = slotIndex;
        Top       = top;
        Height    = height;
    }

    public int Bottom { get { return Top + Height; } } // exclusive
}

// Stacks BlockLayers vertically into one tall Screen and records each block's placement so the caller can
// answer "what slot is at row Y?" and "what rectangle does slot S occupy?". Add a spacer between blocks if
// SpacerRows > 0; spacers don't get a slot mapping.
public sealed class StackLayout
{
    public int Width      { get; }
    public int SpacerRows { get; }

    private readonly List<BlockLayer>      _blocks     = new List<BlockLayer>();
    private readonly List<BlockPlacement>  _placements = new List<BlockPlacement>();
    private int                            _totalRows;

    public StackLayout(int width, int spacerRows)
    {
        Width      = width;
        SpacerRows = spacerRows;
    }

    public int TotalRows { get { return _totalRows; } }
    public IReadOnlyList<BlockPlacement> Placements { get { return _placements; } }

    public void Add(BlockLayer block)
    {
        int top = _totalRows;
        _blocks.Add(block);
        _placements.Add(new BlockPlacement(block.SlotIndex, top, block.Height));
        _totalRows += block.Height + SpacerRows;
    }

    // Returns the slot index at the given composite row, or null if the row is a spacer / out of range.
    public int? SlotAtRow(int row)
    {
        if (row < 0 || row >= _totalRows) return null;
        foreach (BlockPlacement p in _placements)
        {
            if (row >= p.Top && row < p.Bottom) return p.SlotIndex;
        }
        return null;
    }

    public BlockPlacement? PlacementOfSlot(int slotIndex)
    {
        foreach (BlockPlacement p in _placements)
        {
            if (p.SlotIndex == slotIndex) return p;
        }
        return null;
    }

    // Renders only the rows visible in [viewTop, viewTop+viewportHeight) into a fresh viewport-sized Screen.
    // Cost scales with what is visible, not with the full history height: a forward scan skips blocks above
    // the window, blits each block that intersects it (clipped to the window), and stops at the first block
    // past the bottom. viewTop is in composite-row coordinates (rows from the top of the full stack).
    public Screen RenderWindow(int viewportHeight, int viewTop, Cell background)
    {
        Screen viewport = new Screen(Width, viewportHeight, background);
        if (viewportHeight <= 0) return viewport;

        int viewBottom = viewTop + viewportHeight;

        for (int i = 0; i < _placements.Count; i++)
        {
            BlockPlacement p = _placements[i];
            if (p.Bottom <= viewTop) continue;  // entirely above the window
            if (p.Top >= viewBottom) break;      // entirely below; placements are top-ascending, so done

            // Intersect the block with the window, then map to block-local (srcY) and viewport (dstY) rows.
            int srcTop    = p.Top > viewTop ? p.Top : viewTop;
            int srcBottom = p.Bottom < viewBottom ? p.Bottom : viewBottom;
            int srcY      = srcTop - p.Top;
            int dstY      = srcTop - viewTop;
            int rows      = srcBottom - srcTop;

            viewport.Blit(_blocks[i].Current, 0, dstY, BlendMode.Normal, new Rect(0, srcY, Width, rows));
        }

        return viewport;
    }
}
