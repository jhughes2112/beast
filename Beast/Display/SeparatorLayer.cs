using System;


// Builds the separator row Screen, including the busy animation overlay.
// Move BusyWords/BusyAnimations here so DisplayScreen only wires up state; the separator
// can be disabled by commenting out the Blit call in DisplayScreen.Redraw.
internal static class SeparatorLayer
{
    internal static readonly string[] BusyWords = new string[]
    {
        "Rampaging", "Burninating", "Mauling", "Howling", "Stampeding", "Pouncing",
        "Ripping", "Devouring", "Chomping", "Gnashing", "Roaring", "Thundering",
        "Smashing", "Wrecking", "Ravaging", "Preying", "Stalking", "Charging",
        "Attacking", "Clawing", "Biting", "Tearing", "Feasting", "Unleashing",
        "Slashing", "Goring", "Gnawing", "Lunging", "Trampling", "Swooping",
        "Burrowing", "Rending", "Pulverizing", "Sprinting", "Prowling", "Hunting",
        "Snarling", "Hissing", "Snapping", "Striking", "Swiping", "Thrashing",
        "Galloping", "Bolting", "Skulking", "Slithering", "Lurking", "Scuttling",
        "Grappling", "Pinning", "Tossing", "Hurling", "Screeching", "Shrieking",
        "Crunching", "Grinding", "Butting", "Ramming", "Pecking", "Tracking",
        "Scouring", "Foraging", "Scavenging", "Obliterating", "Annihilating",
        "Flattening", "Demolishing", "Rupturing", "Piercing", "Impaling",
        "Skewering", "Slicing", "Cleaving", "Hacking", "Hewing", "Bashing",
        "Pummeling", "Flailing", "Surging", "Seething", "Churning", "Whirling",
        "Splintering", "Shattering", "Bursting", "Exploding", "Blasting", "Torching",
        "Toppling", "Crushing", "Crumbling", "Leveling", "Uprooting", "Devastating",
        "Submerging", "Melting", "Vaporizing", "Disintegrating", "Decimating", "Quaking",
        "Trembling", "Splitting", "Catapulting", "Launching", "Tumbling", "Crashing",
        "Bombarding", "Engulfing", "Swallowing", "Drowning", "Smothering", "Singeing",
        "Searing", "Scorching", "Incinerating", "Moltening", "Corking", "Plunging",
        "Diving", "Scaling", "Ascending", "Descending", "Encroaching", "Invading"
    };

    // Each entry is one animation: an array of frames cycled in order. The frame count is arbitrary —
    // Build() moduloes by the animation's own length — so animations can be as short or as lengthy as you
    // like. The one rule: every frame within a single animation must be the same visible width (use only
    // single-cell glyphs, no emoji) so the word that follows the spinner doesn't jitter as it cycles.
    internal static readonly string[][] BusyAnimations = new string[][]
    {
        new[] { "●∙∙∙", "∙●∙∙", "∙∙●∙", "∙∙∙●", "∙∙●∙", "∙●∙∙" }, // Worm
        new[] { "∙∙∙∙", "●∙∙∙", "●●∙∙", "●●●∙", "●●●●", "∙●●●", "∙∙●●", "∙∙∙●" }, // Growth
        new[] { "⠋   ", " ⠙  ", "  ⠹ ", "   ⠸", "   ⠼", "  ⠴ ", " ⠦  ", "⠧   " }, // Braille chase
        new[] { "←↖↑↗", "↖↑↗→", "↑↗→↘", "↗→↘↓", "→↘↓↙", "↘↓↙←", "↓↙←↖", "↙←↖↑" }, // Arrow wave
        new[] { "    ", "▃   ", "▆▃  ", "█▆▃ ", "▇█▆▃", " ▇█▆", "  ▇█", "   ▇" }, // Pulse bar
        new[] { "▖   ", " ▘  ", "  ▝ ", "   ▗", "  ▝ ", " ▘  " },             // Quadrant scan
        new[] { "◢◣◤◥", "◣◤◥◢", "◤◥◢◣", "◥◢◣◤" },                         // Triangles
        new[] { "||||", "////", "----", "\\\\\\\\" },                        // Rotating pipes (escaped)
        new[] { "◇◇◇◇", "◈◇◇◇", "◆◈◇◇", "◈◆◈◇", "◇◈◆◈", "◇◇◈◆", "◇◇◇◈" },    // Diamond pulse
        new[] { "○◔◑◕", "◔◑◕●", "◑◕●◕", "◕●◕◑", "●◕◑◔", "◕◑◔○" },           // Moon cycle
        new[] { "▐░▒▓", "░▒▓█", "▒▓█▓", "▓█▓▒", "█▓▒░", "▓▒░▐" },           // Density wave
        new[] { "⊶⊷⊶⊷", "⊷⊶⊷⊶" },                                         // Oscillation
        new[] { "◜◠◝◞", "◠◝◞◡", "◝◞◡◟", "◞◡◟◜", "◡◟◜◠", "◟◜◠◝" },           // Arc flow
        new[] { "⌞⌜⌝⌟", "⌜⌝⌟⌞", "⌝⌟⌞⌜", "⌟⌞⌜⌝" },                         // Corner spin
        new[] { "[●  ]", "[ ● ]", "[  ●]", "[ ● ]" },                     // Scanner
        new[] { "{  }", " { }", "{  }", " { }" },                         // Pulse brackets
        new[] { "<  >", "< > ", " >< ", "< > " },                         // Jaws
        new[] { "v   ", " v  ", "  v ", "   v", "  ^ ", " ^  " },          // Gravity bounce
        new[] { "◰◱◲◳", "◱◲◳◰", "◲◳◰◱", "◳◰◱◲" },                         // Box corners
        new[] { "◴◵◶◷", "◵◶◷◴", "◶◷◴◵", "◷◴◵◶" },                         // Clock rotate
        new[] { "⠐⠠⢀⡀", "⠠⢀⡀⠐", "⢀⡀⠐⠠", "⡀⠐⠠⢀" },                   // Marquee
        new[] { "⠁⠂⠄⡀", "⠂⠄⡀⠠", "⠄⡀⠠⠐", "⡀⠠⠐⠈" },                   // Staircase

        // --- Single-cell spinners (width 1) ---
        new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" },     // Braille spin
        new[] { "◐", "◓", "◑", "◒" },                                     // Half-circle spin
        new[] { "◜", "◝", "◞", "◟" },                                     // Arc corners spin
        new[] { "○", "◔", "◑", "◕", "●", "◕", "◑", "◔" },                 // Moon phases
        new[] { "∙", "•", "●", "◉", "●", "•" },                           // Fisheye pulse
        new[] { "✶", "✷", "✸", "✹", "✺", "✹", "✸", "✷" },                 // Star twinkle
        new[] { "⠁", "⠂", "⠄", "⡀", "⠄", "⠂" },                           // Gravity drip
        new[] { "▁", "▂", "▃", "▄", "▅", "▆", "▇", "█", "▇", "▆", "▅", "▄", "▃", "▂" }, // Equalizer column

        // --- Lengthy multi-cell animations ---
        new[] { "●       ", " ●      ", "  ●     ", "   ●    ", "    ●   ", "     ●  ", "      ● ", "       ●", "      ● ", "     ●  ", "    ●   ", "   ●    ", "  ●     ", " ●      " }, // Bouncing ball
        new[] { "▰▱▱▱▱▱▱▱▱▱", "▱▰▱▱▱▱▱▱▱▱", "▱▱▰▱▱▱▱▱▱▱", "▱▱▱▰▱▱▱▱▱▱", "▱▱▱▱▰▱▱▱▱▱", "▱▱▱▱▱▰▱▱▱▱", "▱▱▱▱▱▱▰▱▱▱", "▱▱▱▱▱▱▱▰▱▱", "▱▱▱▱▱▱▱▱▰▱", "▱▱▱▱▱▱▱▱▱▰", "▱▱▱▱▱▱▱▱▰▱", "▱▱▱▱▱▱▱▰▱▱", "▱▱▱▱▱▱▰▱▱▱", "▱▱▱▱▱▰▱▱▱▱", "▱▱▱▱▰▱▱▱▱▱", "▱▱▱▰▱▱▱▱▱▱", "▱▱▰▱▱▱▱▱▱▱", "▱▰▱▱▱▱▱▱▱▱" }, // Knight Rider
        new[] { "▁▂▃▄▅▆▇█", "▂▃▄▅▆▇█▇", "▃▄▅▆▇█▇▆", "▄▅▆▇█▇▆▅", "▅▆▇█▇▆▅▄", "▆▇█▇▆▅▄▃", "▇█▇▆▅▄▃▂", "█▇▆▅▄▃▂▁", "▇▆▅▄▃▂▁▂", "▆▅▄▃▂▁▂▃", "▅▄▃▂▁▂▃▄", "▄▃▂▁▂▃▄▅", "▃▂▁▂▃▄▅▆", "▂▁▂▃▄▅▆▇" }, // Scrolling wave
        new[] { "▶▷▷▷▷▷", "▷▶▷▷▷▷", "▷▷▶▷▷▷", "▷▷▷▶▷▷", "▷▷▷▷▶▷", "▷▷▷▷▷▶" }, // Arrow march
        new[] { "╲   ╱", " ╲ ╱ ", "  ╳  ", " ╱ ╲ ", "╱   ╲", " ╱ ╲ ", "  ╳  ", " ╲ ╱ " }, // Weaving helix
        new[] { "━━━━━━━━━✦", "━━━━━━━━✦ ", "━━━━━━━✦  ", "━━━━━━✦   ", "━━━━━✦    ", "━━━━✦     ", "━━━✦      ", "━━✦       ", "━✦        ", "✦         ", "          ", "    ✸     ", "          " }, // Burning fuse
        new[] { "⊙         ", "·⊙        ", "··⊙       ", " ··⊙      ", "  ··⊙     ", "   ··⊙    ", "    ··⊙   ", "     ··⊙  ", "      ··⊙ ", "       ··⊙" }, // Comet
        new[] { "      ", "▰     ", "▰▰    ", "▰▰▰   ", "▰▰▰▰  ", "▰▰▰▰▰ ", "▰▰▰▰▰▰", "▱▰▰▰▰▰", "▱▱▰▰▰▰", "▱▱▱▰▰▰", "▱▱▱▱▰▰", "▱▱▱▱▱▰", "▱▱▱▱▱▱" }, // Fill and drain
        new[] { "●           ", " ●          ", "  ●         ", "   ●        ", "    ●       ", "     ●      ", "      ●     ", "       ●    ", "        ●   ", "         ●  ", "          ● ", "           ●", "          ● ", "         ●  ", "        ●   ", "       ●    ", "      ●     ", "     ●      ", "    ●       ", "   ●        ", "  ●         ", " ●          " } // Long ping-pong
    };

    internal static int AnimationCount => BusyAnimations.Length;

    // Builds the 1-row separator Screen. Idle: plain horizontal rule. Busy: animated label on the left.
    // The right end always carries the "{Role} F10(N/T)" status (role in yellow), independent of busy state.
    internal static Screen Build(int w, bool agentBusy, long busyStartTick, int busyWordIndex, int currentAnimationIndex, string role, int sessionActive, int sessionTotal)
    {
        Screen sep = new Screen(w, 1, new Cell('─', DisplayScreen.Palette.BrightWhite, DisplayScreen.Palette.Background, CellStyle.None));

        DrawRightStatus(sep, w, role, sessionActive, sessionTotal);

        if (!agentBusy)
            return sep;

        long elapsed = Environment.TickCount64 - busyStartTick;
        string[] anim = BusyAnimations[currentAnimationIndex % BusyAnimations.Length];
        int frameIdx = (int)(elapsed / 125) % anim.Length;
        string frames = anim[frameIdx];
        string word   = BusyWords[busyWordIndex % BusyWords.Length];

        TimeSpan ts = TimeSpan.FromMilliseconds(elapsed);
        string timeLabel = ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : ts.TotalMinutes >= 1
                ? $"{ts.Minutes}:{ts.Seconds:D2}"
                : $"{ts.TotalSeconds:F1}s";

        string label = $" {frames} {word} {timeLabel} ";
        Rgb busyFg = new Rgb(126, 192, 196);
        AnsiToScreen.WriteLine(sep, 0, 0, label, busyFg, DisplayScreen.Palette.Background);

        return sep;
    }

    // Renders "{Role} F10(N/T)" right-aligned on the separator: the role segment in yellow, the
    // F10 hint in grey, with a leading/trailing space so it reads clearly against the rule.
    private static void DrawRightStatus(Screen sep, int w, string role, int sessionActive, int sessionTotal)
    {
        if (sessionTotal <= 0)
            return;

        string hint = $"F10({sessionActive}/{sessionTotal})";
        bool hasRole = !string.IsNullOrEmpty(role);
        string roleSeg = hasRole ? role + " " : "";
        string text = $" {roleSeg}{hint} ";

        int startCol = w - text.Length;
        if (startCol < 0)
            return;

        // Lay down the full text (including the clearing spaces and the grey hint) in grey first,
        // then overwrite just the role characters in yellow.
        AnsiToScreen.WriteLine(sep, startCol, 0, text, DisplayScreen.Palette.MedGrey, DisplayScreen.Palette.Background);
        if (hasRole)
            AnsiToScreen.WriteLine(sep, startCol + 1, 0, role, DisplayScreen.Palette.Yellow, DisplayScreen.Palette.Background);
    }
}
