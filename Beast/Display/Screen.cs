using System;


// 24-bit RGB color. Cells store Fg/Bg as nullable Rgb? where null = transparent (pass-through during blit).
public readonly struct Rgb
{
    public readonly byte R;
    public readonly byte G;
    public readonly byte B;

    public Rgb(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public static Rgb Black   => new Rgb(0, 0, 0);
    public static Rgb White   => new Rgb(255, 255, 255);

    // Per-channel multiply by [0..1+] factor, saturating at 255.
    public Rgb Scale(float factor)
    {
        int r = (int)(R * factor);
        int g = (int)(G * factor);
        int b = (int)(B * factor);
        if (r < 0) r = 0; else if (r > 255) r = 255;
        if (g < 0) g = 0; else if (g > 255) g = 255;
        if (b < 0) b = 0; else if (b > 255) b = 255;
        return new Rgb((byte)r, (byte)g, (byte)b);
    }

    // Linear interpolate toward other by amount [0..1].
    public Rgb Lerp(Rgb other, float amount)
    {
        if (amount <= 0f) return this;
        if (amount >= 1f) return other;
        float inv = 1f - amount;
        return new Rgb(
            (byte)(R * inv + other.R * amount),
            (byte)(G * inv + other.G * amount),
            (byte)(B * inv + other.B * amount));
    }

    public Rgb Invert()
    {
        return new Rgb((byte)(255 - R), (byte)(255 - G), (byte)(255 - B));
    }
}

[Flags]
public enum CellStyle : byte
{
    None      = 0,
    Bold      = 1 << 0,
    Italic    = 1 << 1,
    Underline = 1 << 2
}

// A single terminal cell. Ch is a printable char (space for blank). Fg/Bg null means transparent.
public readonly struct Cell
{
    public readonly char       Ch;
    public readonly Rgb?       Fg;
    public readonly Rgb?       Bg;
    public readonly CellStyle  Style;

    public Cell(char ch, Rgb? fg, Rgb? bg, CellStyle style)
    {
        Ch    = ch;
        Fg    = fg;
        Bg    = bg;
        Style = style;
    }

    // A fully transparent cell — leaves the destination unchanged during a Normal blit.
    public static Cell Transparent => new Cell('\0', null, null, CellStyle.None);

    // A blank cell with the given background, foreground space char.
    public static Cell Blank(Rgb? fg, Rgb? bg)
    {
        return new Cell(' ', fg, bg, CellStyle.None);
    }

    public bool IsTransparent
    {
        get { return Ch == '\0' && Fg == null && Bg == null; }
    }
}

// Inclusive-exclusive rectangle in cell coordinates. (X,Y) is top-left; (X+W, Y+H) is bottom-right exclusive.
public readonly struct Rect
{
    public readonly int X;
    public readonly int Y;
    public readonly int W;
    public readonly int H;

    public Rect(int x, int y, int w, int h)
    {
        X = x;
        Y = y;
        W = w < 0 ? 0 : w;
        H = h < 0 ? 0 : h;
    }

    public int Right  { get { return X + W; } }
    public int Bottom { get { return Y + H; } }

    public bool Contains(int px, int py)
    {
        return px >= X && px < X + W && py >= Y && py < Y + H;
    }

    public Rect Intersect(Rect other)
    {
        int x1 = X > other.X ? X : other.X;
        int y1 = Y > other.Y ? Y : other.Y;
        int x2 = (X + W) < (other.X + other.W) ? (X + W) : (other.X + other.W);
        int y2 = (Y + H) < (other.Y + other.H) ? (Y + H) : (other.Y + other.H);
        if (x2 <= x1 || y2 <= y1) return new Rect(0, 0, 0, 0);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }
}

public enum BlendMode
{
    // Source replaces destination where source is non-transparent. Per-channel: Fg/Bg null = pass-through.
    Normal,
    // Multiply src and dst RGB channels (treats null fg/bg as identity).
    Multiply,
    // 1 - (1-src)*(1-dst) per channel.
    Screen,
    // Additive clamp.
    Add
}

// A WxH rectangular grid of Cells. Cheap to construct; mutate via Set/WriteText/Blit; consume non-destructively
// by blitting it onto another Screen.
public sealed class Screen
{
    public int W { get; }
    public int H { get; }

    private readonly Cell[] _cells;

    public Screen(int w, int h, Cell fill)
    {
        if (w < 0) w = 0;
        if (h < 0) h = 0;
        W = w;
        H = h;
        _cells = new Cell[w * h];
        if (w > 0 && h > 0)
        {
            for (int i = 0; i < _cells.Length; i++)
                _cells[i] = fill;
        }
    }

    public Screen(int w, int h) : this(w, h, Cell.Transparent) { }

    public Cell Get(int x, int y)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return Cell.Transparent;
        return _cells[y * W + x];
    }

    public void Set(int x, int y, Cell cell)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        _cells[y * W + x] = cell;
    }

    public Rect Bounds
    {
        get { return new Rect(0, 0, W, H); }
    }

    // Fill a rectangle with a single cell value.
    public void Fill(Rect rect, Cell cell)
    {
        Rect clipped = rect.Intersect(Bounds);
        for (int y = clipped.Y; y < clipped.Bottom; y++)
        {
            int rowBase = y * W;
            for (int x = clipped.X; x < clipped.Right; x++)
                _cells[rowBase + x] = cell;
        }
    }

    // Writes a single-line string starting at (x,y). Newlines and tabs are not handled; caller pre-processes.
    // Out-of-bounds characters are clipped silently. Returns the X position past the last written char.
    public int WriteText(int x, int y, string text, Rgb? fg, Rgb? bg, CellStyle style)
    {
        if (y < 0 || y >= H) return x;
        int rowBase = y * W;
        int cx = x;
        foreach (char c in text)
        {
            if (cx >= W) break;
            if (cx >= 0)
                _cells[rowBase + cx] = new Cell(c, fg, bg, style);
            cx++;
        }
        return cx;
    }

    // Non-destructive style helper: writes blanks with only a background (Fg null), useful for highlight bands.
    public void FillBackground(Rect rect, Rgb bg)
    {
        Rect clipped = rect.Intersect(Bounds);
        for (int y = clipped.Y; y < clipped.Bottom; y++)
        {
            int rowBase = y * W;
            for (int x = clipped.X; x < clipped.Right; x++)
            {
                Cell existing = _cells[rowBase + x];
                _cells[rowBase + x] = new Cell(existing.Ch == '\0' ? ' ' : existing.Ch, existing.Fg, bg, existing.Style);
            }
        }
    }

    // Blits a source Screen onto this Screen with the given blend mode.
    // srcRect == null means the entire source.
    public void Blit(Screen src, int dstX, int dstY, BlendMode mode, Rect? srcRect)
    {
        Rect sr = srcRect.HasValue ? srcRect.Value.Intersect(src.Bounds) : src.Bounds;
        if (sr.W == 0 || sr.H == 0) return;

        // Destination rectangle in this screen's coordinates.
        Rect dr = new Rect(dstX, dstY, sr.W, sr.H).Intersect(Bounds);
        if (dr.W == 0 || dr.H == 0) return;

        int srcOffsetX = sr.X + (dr.X - dstX);
        int srcOffsetY = sr.Y + (dr.Y - dstY);

        for (int row = 0; row < dr.H; row++)
        {
            int dyRow = (dr.Y + row) * W;
            int syRow = (srcOffsetY + row) * src.W;
            for (int col = 0; col < dr.W; col++)
            {
                Cell s = src._cells[syRow + (srcOffsetX + col)];
                int di = dyRow + (dr.X + col);
                _cells[di] = BlendCell(_cells[di], s, mode);
            }
        }
    }

    private static Cell BlendCell(Cell dst, Cell src, BlendMode mode)
    {
        if (src.IsTransparent) return dst;

        char outCh        = src.Ch != '\0' ? src.Ch : dst.Ch;
        CellStyle outStyle = src.Style != CellStyle.None ? src.Style : dst.Style;
        Rgb? outFg;
        Rgb? outBg;

        if (mode == BlendMode.Normal)
        {
            outFg = src.Fg.HasValue ? src.Fg : dst.Fg;
            outBg = src.Bg.HasValue ? src.Bg : dst.Bg;
        }
        else
        {
            outFg = BlendChannel(dst.Fg, src.Fg, mode);
            outBg = BlendChannel(dst.Bg, src.Bg, mode);
        }

        return new Cell(outCh == '\0' ? ' ' : outCh, outFg, outBg, outStyle);
    }

    private static Rgb? BlendChannel(Rgb? dst, Rgb? src, BlendMode mode)
    {
        if (!src.HasValue) return dst;
        if (!dst.HasValue) return src;
        Rgb d = dst.Value;
        Rgb s = src.Value;
        switch (mode)
        {
            case BlendMode.Multiply:
                return new Rgb(
                    (byte)(d.R * s.R / 255),
                    (byte)(d.G * s.G / 255),
                    (byte)(d.B * s.B / 255));
            case BlendMode.Screen:
                return new Rgb(
                    (byte)(255 - ((255 - d.R) * (255 - s.R) / 255)),
                    (byte)(255 - ((255 - d.G) * (255 - s.G) / 255)),
                    (byte)(255 - ((255 - d.B) * (255 - s.B) / 255)));
            case BlendMode.Add:
                int r = d.R + s.R; if (r > 255) r = 255;
                int g = d.G + s.G; if (g > 255) g = 255;
                int b = d.B + s.B; if (b > 255) b = 255;
                return new Rgb((byte)r, (byte)g, (byte)b);
            default:
                return src;
        }
    }
}
