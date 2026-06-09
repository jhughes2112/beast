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
        new[] { "<  >", "<==>", " <  >", "  <  >" },                      // Jaws
        new[] { "v   ", " v  ", "  v ", "   v", "  ^ ", " ^  " },          // Gravity bounce
        new[] { "◰◱◲◳", "◱◲◳◰", "◲◳◰◱", "◳◰◱◲" },                         // Box corners
        new[] { "◴◵◶◷", "◵◶◷◴", "◶◷◴◵", "◷◴◵◶" },                         // Clock rotate
        new[] { "⠐⠠⢀⡀", "⠠⢀⡀⠐", "⢀⡀⠐⠠", "⡀⠐⠠⢀" },                   // Marquee
        new[] { "⠁⠂⠄⡀", "⠂⠄⡀⠠", "⠄⡀⠠⠐", "⡀⠠⠐⠈" }                    // Staircase
    };

    internal static int AnimationCount => BusyAnimations.Length;

    // Builds the 1-row separator Screen. Idle: plain horizontal rule. Busy: animated label on the left.
    internal static Screen Build(int w, bool agentBusy, long busyStartTick, int busyWordIndex, int currentAnimationIndex)
    {
        Screen sep = new Screen(w, 1, new Cell('─', DisplayScreen.Palette.BrightWhite, DisplayScreen.Palette.Background, CellStyle.None));
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
        Rgb busyFg = new Rgb(80, 200, 200);
        AnsiToScreen.WriteLine(sep, 0, 0, label, busyFg, DisplayScreen.Palette.Background);

        return sep;
    }
}
