namespace WowLauncher;

/// <summary>
/// Which skin the process is wearing, decided once from the command line in <see cref="Program"/>.
///
///   (default)        v3 — the shipped shell: realm rail, key-art hero, friends sidebar
///                         (Styles.axaml + Styles.v3.axaml)
///   --ui v1          v1 — the first shell: left nav rail, stone + ember   (Styles.axaml)
///   --ui v2          v2 — "Obsidian Instrument": tabs, telemetry column,
///                         hearth backdrop, cyan for machine data          (Styles.v2.axaml)
///   --demo           fill the not-yet-wired telemetry and the friends list with representative
///                    values, so a skin can be judged on a machine with no realm connection
///
/// v3 became the default on 2026-07-21 (owner decision). v1 and v2 stay in the binary and stay
/// reachable by flag: they cost nothing at runtime, and having all three in one build is what let
/// them be compared side by side on the same box in the first place.
/// </summary>
public static class Ui
{
    public static bool V1 { get; private set; }
    public static bool V2 { get; private set; }
    public static bool Demo { get; private set; }

    /// <summary>
    /// True for the QA screenshot harness (<c>--screenshot</c>). That harness renders a real
    /// <see cref="Avalonia.Controls.Window"/> off a headless/offscreen backend and calls
    /// <c>Environment.Exit</c> a few seconds later — it never has a human at the keyboard to answer a
    /// native folder-picker dialog. A picker service must check this BEFORE touching
    /// <c>TopLevel.StorageProvider</c>: on some backends opening the OS dialog there does not throw, it
    /// blocks, which would hang the harness instead of producing a PNG. <c>--e2e</c> never reaches this
    /// flag at all (it exits before <see cref="Configure"/> runs), so it needs no entry here.
    /// </summary>
    public static bool Headless { get; private set; }

    /// <summary>The default shell. Anything that is not an explicit v1/v2 request is v3.</summary>
    public static bool V3 => !V1 && !V2;

    public static void Configure(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var value = args[i] == "--ui" && i + 1 < args.Length ? args[i + 1]
                : args[i].StartsWith("--ui=", System.StringComparison.Ordinal) ? args[i][5..]
                : null;

            if (value is not null)
            {
                if (value.Equals("v1", System.StringComparison.OrdinalIgnoreCase)) V1 = true;
                else if (value.Equals("v2", System.StringComparison.OrdinalIgnoreCase)) V2 = true;
                // "v3" (or anything unknown) leaves both flags false, which IS v3.
            }
            else if (args[i] == "--demo")
            {
                Demo = true;
            }
            else if (args[i] == "--screenshot")
            {
                Headless = true;
            }
        }
    }
}
