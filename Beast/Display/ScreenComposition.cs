using System;
using System.Collections.Generic;


// Represents one conversation block. Holds its slot index plus two pre-rendered Screens for the collapsed
// (one-line summary) and expanded (full content) views. Layout switches between them based on IsExpanded.
// Construct once per (block, terminal-width); rebuild on resize or content change.
public sealed class BlockLayer
{
	public int SlotIndex { get; }
	public Screen Collapsed { get; }
	public Screen Expanded { get; }
	public bool IsExpanded { get; }

	public BlockLayer(int slotIndex, Screen collapsed, Screen expanded, bool isExpanded)
	{
		SlotIndex = slotIndex;
		Collapsed = collapsed;
		Expanded = expanded;
		IsExpanded = isExpanded;
	}

	public Screen Current { get { return IsExpanded ? Expanded : Collapsed; } }
	public int Height { get { return Current.H; } }
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
		Top = top;
		Height = height;
	}

	public int Bottom { get { return Top + Height; } } // exclusive
}

// Stacks BlockLayers vertically into one tall Screen and records each block's placement so the caller can
// answer "what slot is at row Y?" and "what rectangle does slot S occupy?". Add a spacer between blocks if
// SpacerRows > 0; spacers don't get a slot mapping.
public sealed class StackLayout
{
	public int Width { get; }
	public int SpacerRows { get; }

	private readonly List<BlockLayer>      _blocks     = new List<BlockLayer>();
	private readonly List<BlockPlacement>  _placements = new List<BlockPlacement>();
	private int                            _totalRows;

	public StackLayout(int width, int spacerRows)
	{
		Width = width;
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

	// Swaps in a rebuilt block for the same slot when its height is unchanged, leaving every placement
	// valid — the in-place patch for a content change that didn't move anything. Returns false when the
	// slot is absent or the height differs, in which case the caller must rebuild the layout.
	public bool TryReplaceBlock(BlockLayer block)
	{
		bool replaced = false;
		for (int i = 0; i < _placements.Count; i++)
		{
			if (_placements[i].SlotIndex == block.SlotIndex)
			{
				if (_blocks[i].Height == block.Height)
				{
					_blocks[i] = block;
					replaced = true;
				}
				break;
			}
		}
		return replaced;
	}

	// Returns the slot index at the given composite row, or null if the row is a spacer / out of range.
	public int? SlotAtRow(int row)
	{
		if (row < 0 || row >= _totalRows)
			return null;
		foreach (BlockPlacement p in _placements)
		{
			if (row >= p.Top && row < p.Bottom)
				return p.SlotIndex;
		}
		return null;
	}

	public BlockPlacement? PlacementOfSlot(int slotIndex)
	{
		foreach (BlockPlacement p in _placements)
		{
			if (p.SlotIndex == slotIndex)
				return p;
		}
		return null;
	}

	// Width of the block at the given slot's current (collapsed/expanded) view, or 0 if not present. Used to
	// clamp a block's horizontal scroll offset to the range that actually reveals content.
	public int BlockWidthOfSlot(int slotIndex)
	{
		for (int i = 0; i < _placements.Count; i++)
		{
			if (_placements[i].SlotIndex == slotIndex)
				return _blocks[i].Current.W;
		}
		return 0;
	}

	// Renders only the rows visible in [viewTop, viewTop+viewportHeight) into a fresh viewport-sized Screen.
	// Cost scales with what is visible, not with the full history height: a forward scan skips blocks above
	// the window, blits each block that intersects it (clipped to the window), and stops at the first block
	// past the bottom. viewTop is in composite-row coordinates (rows from the top of the full stack).
	// hOffsets maps a slot to its horizontal scroll offset; a block wider than the viewport shows the window
	// [offset, offset+Width). Pass null for no horizontal scrolling.
	public Screen RenderWindow(int viewportHeight, int viewTop, Cell background, IReadOnlyDictionary<int, int>? hOffsets)
	{
		Screen viewport = new Screen(Width, viewportHeight, background);
		if (viewportHeight <= 0)
			return viewport;

		int viewBottom = viewTop + viewportHeight;

		for (int i = 0; i < _placements.Count; i++)
		{
			BlockPlacement p = _placements[i];
			if (p.Bottom <= viewTop)
				continue;  // entirely above the window
			if (p.Top >= viewBottom)
				break;      // entirely below; placements are top-ascending, so done

			// Intersect the block with the window, then map to block-local (srcY) and viewport (dstY) rows.
			int srcTop    = p.Top > viewTop ? p.Top : viewTop;
			int srcBottom = p.Bottom < viewBottom ? p.Bottom : viewBottom;
			int srcY      = srcTop - p.Top;
			int dstY      = srcTop - viewTop;
			int rows      = srcBottom - srcTop;

			// Horizontal window: clamp the offset into the block so it never scrolls past its content.
			int blockW = _blocks[i].Current.W;
			int srcX   = 0;
			if (hOffsets != null && hOffsets.TryGetValue(p.SlotIndex, out int off) && blockW > Width)
			{
				int maxOff = blockW - Width;
				srcX = off < 0 ? 0 : (off > maxOff ? maxOff : off);
			}

			viewport.Blit(_blocks[i].Current, 0, dstY, BlendMode.Normal, new Rect(srcX, srcY, Width, rows));
		}

		return viewport;
	}
}