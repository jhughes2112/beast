using System;
using System.Collections.Generic;


// A single operation in a ScreenCompositor's ordered op list. Implementations are tiny structs/classes
// representing a deferred blit or effect — nothing executes until ScreenCompositor.Render(target) is called.
public interface ICompositionOp
{
    void Execute(Screen target);
}

// Blits a source Screen onto the target at (DstX, DstY) using the given blend mode.
// SrcRect == null means the whole source.
public sealed class BlitOp : ICompositionOp
{
    public Screen    Source   { get; }
    public int       DstX     { get; }
    public int       DstY     { get; }
    public BlendMode Blend    { get; }
    public Rect?     SrcRect  { get; }

    public BlitOp(Screen source, int dstX, int dstY, BlendMode blend, Rect? srcRect)
    {
        Source  = source;
        DstX    = dstX;
        DstY    = dstY;
        Blend   = blend;
        SrcRect = srcRect;
    }

    public void Execute(Screen target)
    {
        target.Blit(Source, DstX, DstY, Blend, SrcRect);
    }
}

// Applies an IScreenEffect to a rectangle of the target Screen.
public sealed class EffectOp : ICompositionOp
{
    public IScreenEffect Effect { get; }
    public Rect          Rect   { get; }

    public EffectOp(IScreenEffect effect, Rect rect)
    {
        Effect = effect;
        Rect   = rect;
    }

    public void Execute(Screen target)
    {
        Effect.Apply(target, Rect);
    }
}

// A Photoshop-style ordered op list. Operations are appended; Render runs them in order against a target.
// Render is idempotent and may be called many times per frame against different targets (e.g. preview vs final).
public sealed class ScreenCompositor
{
    private readonly List<ICompositionOp> _ops = new List<ICompositionOp>();

    public IReadOnlyList<ICompositionOp> Ops { get { return _ops; } }

    public void Add(ICompositionOp op)
    {
        _ops.Add(op);
    }

    public void AddBlit(Screen source, int dstX, int dstY, BlendMode blend, Rect? srcRect)
    {
        _ops.Add(new BlitOp(source, dstX, dstY, blend, srcRect));
    }

    public void AddEffect(IScreenEffect effect, Rect rect)
    {
        _ops.Add(new EffectOp(effect, rect));
    }

    public void Clear()
    {
        _ops.Clear();
    }

    public void Render(Screen target)
    {
        foreach (ICompositionOp op in _ops)
            op.Execute(target);
    }
}

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

    // Builds a fresh tall Screen by blitting each block's Current Screen at its placement row.
    // Returns the composite Screen plus a compositor that produced it, so callers can add overlays/effects.
    public (Screen Composite, ScreenCompositor Compositor) Compose(Cell background)
    {
        Screen target = new Screen(Width, _totalRows, background);
        ScreenCompositor compositor = new ScreenCompositor();
        for (int i = 0; i < _blocks.Count; i++)
        {
            compositor.AddBlit(_blocks[i].Current, 0, _placements[i].Top, BlendMode.Normal, null);
        }
        compositor.Render(target);
        return (target, compositor);
    }
}

// A scrollable viewport into a (typically much taller) source Screen. ScrollOffset is in rows from the top
// of the source. The viewport is itself a Screen of (Width, ViewportHeight) populated by Render().
// MapViewRowToSourceRow / MapSourceRowToViewRow translate mouse coordinates between view and source space.
public sealed class ScreenView
{
    public Screen Source         { get; }
    public int    ViewportHeight { get; }
    public int    ScrollOffset   { get; }

    public ScreenView(Screen source, int viewportHeight, int scrollOffset)
    {
        Source         = source;
        ViewportHeight = viewportHeight < 0 ? 0 : viewportHeight;
        int maxOffset = Math.Max(0, source.H - ViewportHeight);
        if (scrollOffset < 0) scrollOffset = 0;
        if (scrollOffset > maxOffset) scrollOffset = maxOffset;
        ScrollOffset = scrollOffset;
    }

    public int MaxScrollOffset { get { return Math.Max(0, Source.H - ViewportHeight); } }

    public int MapViewRowToSourceRow(int viewRow)
    {
        return viewRow + ScrollOffset;
    }

    public int? MapSourceRowToViewRow(int sourceRow)
    {
        int v = sourceRow - ScrollOffset;
        if (v < 0 || v >= ViewportHeight) return null;
        return v;
    }

    // Renders the visible window of Source into a fresh Screen of (Source.W, ViewportHeight).
    public Screen Render(Cell background)
    {
        Screen viewport = new Screen(Source.W, ViewportHeight, background);
        viewport.Blit(Source, 0, 0, BlendMode.Normal, new Rect(0, ScrollOffset, Source.W, ViewportHeight));
        return viewport;
    }
}
