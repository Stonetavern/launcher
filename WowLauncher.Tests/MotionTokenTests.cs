using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Pins the motion tokens of the 1.9 login/loading shell (Spec 2026-09-20 §3) to the resource sheet
/// <c>WowLauncher/Styles/Motion.axaml</c>, name by name and value by value.
///
/// <para>Source-text assertions, like <see cref="MotionGateTests"/>: the test project carries no
/// Avalonia.Headless reference. The point is not to prove Avalonia parses the sheet (the build does
/// that, the sheet is compiled XAML) but that NOBODY can change a duration, an easing or a distance
/// without the spec changing first. Placebo probe: set <c>Motion.Base</c> to 0:0:0.300 in the sheet
/// and this file goes red.</para>
///
/// <para>The second half enforces the spec's "no ad-hoc durations in code" rule for the files that
/// belong to the new shell: every <c>Duration=</c> and every <c>Easing=</c> attribute in those files
/// must be a <c>{StaticResource …}</c> reference into this sheet, never a literal.</para>
/// </summary>
public sealed partial class MotionTokenTests
{
    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "WowLauncher");
            if (File.Exists(Path.Combine(candidate, "Styles", "Motion.axaml"))) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"WowLauncher source tree not found above {AppContext.BaseDirectory}");
    }

    private static string ReadSource(string relative)
    {
        var path = Path.Combine(AppDir(), relative);
        Assert.True(File.Exists(path), $"expected source file missing: {path}");
        return File.ReadAllText(path);
    }

    private static string StripComments(string xaml) => CommentRegex().Replace(xaml, " ");

    /// <summary>Every <c>&lt;type x:Key="k"&gt;value&lt;/type&gt;</c> and every self-closing
    /// <c>&lt;type x:Key="k" attr="…"/&gt;</c> in the sheet, keyed by x:Key.</summary>
    private static Dictionary<string, (string Type, string Value)> ParseTokens()
    {
        var xaml = StripComments(ReadSource(Path.Combine("Styles", "Motion.axaml")));
        var tokens = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        foreach (Match m in ContentTokenRegex().Matches(xaml))
            tokens[m.Groups["key"].Value] = (m.Groups["type"].Value, m.Groups["value"].Value.Trim());

        foreach (Match m in SelfClosingTokenRegex().Matches(xaml))
        {
            var attrs = m.Groups["attrs"].Value;
            var pairs = AttrRegex().Matches(attrs)
                .Select(a => (Name: a.Groups[1].Value, Value: a.Groups[2].Value))
                .Where(a => a.Name != "x:Key")
                .Select(a => $"{a.Name}={a.Value}");
            tokens[m.Groups["key"].Value] = (m.Groups["type"].Value, string.Join(" ", pairs));
        }

        Assert.NotEmpty(tokens);
        return tokens;
    }

    // ── Spec §3, verbatim ────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Durations() =>
    [
        ["Motion.Fast",       "0:0:0.150"],   // 150 ms  Hover, Fokus, Button-Press
        ["Motion.Base",       "0:0:0.250"],   // 250 ms  Einblenden, Zustandswechsel, Fehler-Feedback
        ["Motion.Slow",       "0:0:0.400"],   // 400 ms  Panel-Wechsel, Karten-Eintritt
        ["Motion.Transition", "0:0:0.450"],   // 450 ms  Phasenwechsel
        ["Motion.Shimmer",    "0:0:2.000"],   // 2000 ms Shimmer-Zyklus
        ["Motion.Pulse",      "0:0:4.000"],   // 4000 ms Sigil-Glow
        ["Motion.Ambient",    "0:0:20.000"],  // 20 s    Sigil-Rotation, Shader-Drift
        // Flaechen-Loader (Owner-Vorlage REF-2026-09-20-quantum-cloud-loader.md): vier Perioden
        ["Motion.QuantumA",   "0:0:3.800"],
        ["Motion.QuantumB",   "0:0:5.600"],
        ["Motion.QuantumC",   "0:0:3.100"],
        ["Motion.QuantumD",   "0:0:6.400"],
    ];

    [Theory]
    [MemberData(nameof(Durations))]
    public void EveryDurationTokenCarriesTheSpecValue(string key, string expected)
    {
        var tokens = ParseTokens();
        Assert.True(tokens.ContainsKey(key), $"token {key} missing from Motion.axaml");
        Assert.Equal("sys:TimeSpan", tokens[key].Type);
        Assert.Equal(TimeSpan.Parse(expected), TimeSpan.Parse(tokens[key].Value));
    }

    public static IEnumerable<object[]> Easings() =>
    [
        ["Ease.Standard", "SplineEasing", "X1=0.4 Y1=0.0 X2=0.2 Y2=1.0"],   // cubic-bezier(0.4, 0.0, 0.2, 1)
        ["Ease.Decel",    "SplineEasing", "X1=0.0 Y1=0.0 X2=0.2 Y2=1.0"],   // cubic-bezier(0.0, 0.0, 0.2, 1)
        ["Ease.Linear",   "LinearEasing", ""],                               // ambient loops run linear
        ["Ease.Quantum",  "SplineEasing", "X1=0.37 Y1=0.0 X2=0.63 Y2=1.0"], // cubic-bezier(0.37, 0, 0.63, 1), the loader template
    ];

    [Theory]
    [MemberData(nameof(Easings))]
    public void EveryEasingTokenCarriesTheSpecCurve(string key, string type, string expected)
    {
        var tokens = ParseTokens();
        Assert.True(tokens.ContainsKey(key), $"token {key} missing from Motion.axaml");
        Assert.Equal(type, tokens[key].Type);
        Assert.Equal(expected, tokens[key].Value);
    }

    public static IEnumerable<object[]> Distances() =>
    [
        ["Motion.EnterOffset",      "sys:Double", "12"],    // Karten-/Listen-Eintritt: 12 px, nie mehr
        ["Motion.ShakeAmplitude",   "sys:Double", "8"],     // Fehler-Shake: max. 8 px
        ["Motion.ShakeCycles",      "sys:Int32",  "2"],     // 2 Zyklen
        ["Motion.TransitionScale",  "sys:Double", "0.98"],  // Scale 0.98 -> 1.0
        ["Motion.SigilRestOpacity", "sys:Double", "0.15"],  // §4: Sigil bei 0.15 im ersten Frame
    ];

    [Theory]
    [MemberData(nameof(Distances))]
    public void EveryDistanceTokenCarriesTheSpecValue(string key, string type, string expected)
    {
        var tokens = ParseTokens();
        Assert.True(tokens.ContainsKey(key), $"token {key} missing from Motion.axaml");
        Assert.Equal(type, tokens[key].Type);
        Assert.Equal(expected, tokens[key].Value);
    }

    [Fact]
    public void TheSheetCarriesExactlyTheSpecTokens_NothingSlippedIn()
    {
        var expected = Durations().Select(d => (string)d[0])
            .Concat(Easings().Select(e => (string)e[0]))
            .Concat(Distances().Select(d => (string)d[0]))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        var actual = ParseTokens().Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
    }

    // ── "Keine Ad-hoc-Dauern im Code" (§3) ───────────────────────────────────────────────────────

    /// <summary>The files that make up the login/loading shell. A file added to the shell is added
    /// here, or its literals go unmeasured.</summary>
    private static readonly string[] ShellFiles =
    [
        Path.Combine("Views", "LoginShellWindow.axaml"),
        Path.Combine("Styles", "LoginShell.axaml"),
        Path.Combine("Controls", "QuantumLoader.axaml"),
    ];

    [Fact]
    public void TheLoginShellUsesOnlyTokenDurationsAndEasings()
    {
        var tokens = ParseTokens();
        var offenders = new List<string>();

        foreach (var file in ShellFiles)
        {
            var xaml = StripComments(ReadSource(file));
            foreach (Match m in DurationOrEasingAttrRegex().Matches(xaml))
            {
                var value = m.Groups["value"].Value;
                var res = Regex.Match(value, @"^\{StaticResource\s+(?<key>[\w.]+)\}$");
                if (!res.Success)
                    offenders.Add($"{file}: {m.Groups["attr"].Value}=\"{value}\" is a literal");
                else if (!tokens.ContainsKey(res.Groups["key"].Value))
                    offenders.Add($"{file}: {m.Groups["attr"].Value} references unknown token {res.Groups["key"].Value}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheLoginShellCodeBehindHasNoLiteralTimeSpans()
    {
        // The Phase-3 storyboard and the status-line crossfade are driven from C#; the durations they
        // wait on must come from the same sheet. A TimeSpan.FromMilliseconds(450) in code would be a
        // second, unpinned copy of Motion.Transition.
        string[] codeFiles =
        [
            Path.Combine("Views", "LoginShellWindow.axaml.cs"),
            Path.Combine("ViewModels", "LoginShellViewModel.cs"),
        ];
        var offenders = new List<string>();
        foreach (var file in codeFiles)
        {
            var src = ReadSource(file);
            foreach (Match m in Regex.Matches(src, @"TimeSpan\.From(Milliseconds|Seconds)\(\s*\d"))
                offenders.Add($"{file}: {m.Value}…");
        }
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex("<(?<type>[\\w:]+)\\s+x:Key=\"(?<key>[\\w.]+)\"\\s*>(?<value>[^<]*)</\\k<type>>")]
    private static partial Regex ContentTokenRegex();

    [GeneratedRegex("<(?<type>[\\w:]+)\\s+x:Key=\"(?<key>[\\w.]+)\"(?<attrs>[^<>]*?)/>")]
    private static partial Regex SelfClosingTokenRegex();

    [GeneratedRegex("([\\w:]+)=\"([^\"]*)\"")]
    private static partial Regex AttrRegex();

    [GeneratedRegex("\\b(?<attr>Duration|Easing)\\s*=\\s*\"(?<value>[^\"]*)\"")]
    private static partial Regex DurationOrEasingAttrRegex();
}
