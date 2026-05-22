using System;


// An effect mutates the cells of a target Screen inside a rectangle.
// Effects are non-destructive at the composition level: they are stored as ScreenCompositor ops and only
// applied when the compositor renders into a target Screen.
public interface IScreenEffect
{
    void Apply(Screen target, Rect rect);
}

// Multiplies Fg/Bg RGB by Factor (1.0 = unchanged, <1 darker, >1 brighter). Skips transparent cells.
public sealed class BrightnessEffect : IScreenEffect
{
    public float Factor { get; }

    public BrightnessEffect(float factor)
    {
        Factor = factor;
    }

    public void Apply(Screen target, Rect rect)
    {
        Rect r = rect.Intersect(target.Bounds);
        for (int y = r.Y; y < r.Bottom; y++)
        {
            for (int x = r.X; x < r.Right; x++)
            {
                Cell c = target.Get(x, y);
                Rgb? fg = c.Fg.HasValue ? c.Fg.Value.Scale(Factor) : c.Fg;
                Rgb? bg = c.Bg.HasValue ? c.Bg.Value.Scale(Factor) : c.Bg;
                target.Set(x, y, new Cell(c.Ch, fg, bg, c.Style));
            }
        }
    }
}

// Like BrightnessEffect but with independent factors for foreground and background channels.
// Useful when a hover/glow should emphasize the text strongly while only nudging the background.
public sealed class ChannelBrightnessEffect : IScreenEffect
{
    public float FgFactor { get; }
    public float BgFactor { get; }

    public ChannelBrightnessEffect(float fgFactor, float bgFactor)
    {
        FgFactor = fgFactor;
        BgFactor = bgFactor;
    }

    public void Apply(Screen target, Rect rect)
    {
        Rect r = rect.Intersect(target.Bounds);
        for (int y = r.Y; y < r.Bottom; y++)
        {
            for (int x = r.X; x < r.Right; x++)
            {
                Cell c = target.Get(x, y);
                Rgb? fg = c.Fg.HasValue ? c.Fg.Value.Scale(FgFactor) : c.Fg;
                Rgb? bg = c.Bg.HasValue ? c.Bg.Value.Scale(BgFactor) : c.Bg;
                target.Set(x, y, new Cell(c.Ch, fg, bg, c.Style));
            }
        }
    }
}

// Lerps Fg/Bg toward Color by Amount [0..1].
public sealed class TintEffect : IScreenEffect
{
    public Rgb   Color  { get; }
    public float Amount { get; }

    public TintEffect(Rgb color, float amount)
    {
        Color  = color;
        Amount = amount;
    }

    public void Apply(Screen target, Rect rect)
    {
        Rect r = rect.Intersect(target.Bounds);
        for (int y = r.Y; y < r.Bottom; y++)
        {
            for (int x = r.X; x < r.Right; x++)
            {
                Cell c = target.Get(x, y);
                Rgb? fg = c.Fg.HasValue ? c.Fg.Value.Lerp(Color, Amount) : c.Fg;
                Rgb? bg = c.Bg.HasValue ? c.Bg.Value.Lerp(Color, Amount) : c.Bg;
                target.Set(x, y, new Cell(c.Ch, fg, bg, c.Style));
            }
        }
    }
}

public sealed class InvertEffect : IScreenEffect
{
    public void Apply(Screen target, Rect rect)
    {
        Rect r = rect.Intersect(target.Bounds);
        for (int y = r.Y; y < r.Bottom; y++)
        {
            for (int x = r.X; x < r.Right; x++)
            {
                Cell c = target.Get(x, y);
                Rgb? fg = c.Fg.HasValue ? c.Fg.Value.Invert() : c.Fg;
                Rgb? bg = c.Bg.HasValue ? c.Bg.Value.Invert() : c.Bg;
                target.Set(x, y, new Cell(c.Ch, fg, bg, c.Style));
            }
        }
    }
}

// Lerps only the background toward Color by Amount [0..1]. Foreground untouched.
// Use when a hover/glow needs to be visible even when the underlying bg is near-black
// (multiplicative brightness cannot brighten a true-black cell).
public sealed class BackgroundTintEffect : IScreenEffect
{
    public Rgb   Color  { get; }
    public float Amount { get; }

    public BackgroundTintEffect(Rgb color, float amount)
    {
        Color  = color;
        Amount = amount;
    }

    public void Apply(Screen target, Rect rect)
    {
        Rect r = rect.Intersect(target.Bounds);
        for (int y = r.Y; y < r.Bottom; y++)
        {
            for (int x = r.X; x < r.Right; x++)
            {
                Cell c = target.Get(x, y);
                Rgb bgBase = c.Bg.HasValue ? c.Bg.Value : Rgb.Black;
                Rgb bg = bgBase.Lerp(Color, Amount);
                target.Set(x, y, new Cell(c.Ch, c.Fg, bg, c.Style));
            }
        }
    }
}

// Replaces every cell's background with the given color (foreground untouched). Useful for highlight bands.
public sealed class FillBackgroundEffect : IScreenEffect
{
    public Rgb Color { get; }

    public FillBackgroundEffect(Rgb color)
    {
        Color = color;
    }

    public void Apply(Screen target, Rect rect)
    {
        target.FillBackground(rect, Color);
    }
}
