using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WowLauncher.Localization;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Gates the UI string catalog. Deliberately not smoke tests: each one fails loudly for a failure
/// mode that would otherwise ship green.
///
/// <list type="bullet">
/// <item>The catalog is an <c>EmbeddedResource</c>. If the csproj glob stops matching, every label in
/// the app silently becomes its own key and the app still runs.
/// <see cref="Catalog_is_embedded_and_substantial"/> turns that into a red test.</item>
/// <item>A typo in <c>{loc:Tr Play_Nwes}</c> compiles fine and renders the raw key on screen.
/// <see cref="Every_key_used_in_code_or_markup_exists_in_the_catalog"/> catches it.</item>
/// <item>Strings deleted from the UI but left in the catalog rot into misleading translation work.
/// <see cref="Catalog_has_no_unused_keys"/> catches that.</item>
/// </list>
/// </summary>
public sealed partial class LocalizationCatalogTests
{
    /// <summary><c>{loc:Tr Some_Key}</c> in AXAML, <c>Loc.T("Some_Key")</c> / <c>Loc.F("Some_Key"</c> in C#.</summary>
    private static readonly Regex KeyUse =
        MyRegex();

    private static Dictionary<string, string> Catalog()
    {
        var catalog = Loc.Load(Loc.Code);
        Assert.True(catalog is not null,
            "en.json is not embedded in the assembly. Check the EmbeddedResource glob in " +
            "WowLauncher.csproj - without it every label in the UI degrades to its raw key.");
        return catalog!;
    }

    /// <summary>Comment/metadata entries are not user-facing content.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Content(Dictionary<string, string> c) =>
        c.Where(kv => !kv.Key.StartsWith('_'));

    /// <summary>The app project directory, walked up from the test binary.</summary>
    private static DirectoryInfo AppSources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "WowLauncher", "Localization")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the WowLauncher project from the test binary.");
        return new DirectoryInfo(Path.Combine(dir!.FullName, "WowLauncher"));
    }

    private static Dictionary<string, List<string>> UsedKeys()
    {
        var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var files = AppSources()
            .EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(f => f.Extension is ".cs" or ".axaml")
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        foreach (var file in files)
        {
            foreach (Match m in KeyUse.Matches(File.ReadAllText(file.FullName)))
            {
                var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!used.TryGetValue(key, out var where)) used[key] = where = [];
                if (!where.Contains(file.Name)) where.Add(file.Name);
            }
        }

        return used;
    }

    [Fact]
    public void Catalog_is_embedded_and_substantial()
    {
        // Not "> 0": a nearly empty catalog would pass that while the UI runs on raw keys.
        var count = Content(Catalog()).Count();
        Assert.True(count > 100, $"Catalog holds only {count} strings.");
    }

    [Fact]
    public void Every_key_used_in_code_or_markup_exists_in_the_catalog()
    {
        var catalog = Catalog();

        var missing = UsedKeys()
            .Where(kv => !catalog.ContainsKey(kv.Key))
            .Select(kv => $"{kv.Key} (used in {string.Join(", ", kv.Value)})")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These keys are referenced but not in en.json, so they render as raw keys on screen:\n" +
            string.Join("\n", missing));
    }

    [Fact]
    public void Catalog_has_no_unused_keys()
    {
        var used = UsedKeys();

        var dead = Content(Catalog())
            .Select(kv => kv.Key)
            .Where(k => !used.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(dead.Count == 0,
            "These catalog keys are not referenced anywhere. Remove them or wire them up:\n" +
            string.Join("\n", dead));
    }

    /// <summary>VOICE.md: player-facing ENGLISH carries no em dashes, no en dashes and no
    /// apostrophes (so no contractions either). Scoped to English on purpose - French cannot be
    /// written without apostrophes, and demanding it there would produce broken French to satisfy a
    /// rule about English.</summary>
    [Fact]
    public void Catalog_follows_voice_rules()
    {
        var offenders = Content(Catalog())
            .Where(kv => kv.Value.Contains('—') || kv.Value.Contains('–')
                      || kv.Value.Contains('\'') || kv.Value.Contains('’'))
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "VOICE.md forbids em/en dashes and apostrophes in player-facing text:\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void Unknown_key_renders_as_the_key_itself()
    {
        // Loud, not empty: a typo shows up on screen instead of a blank label.
        Assert.Equal("Nope_Not_A_Key", Loc.T("Nope_Not_A_Key"));
    }

    [Fact]
    public void Format_placeholders_resolve()
    {
        Assert.Equal("Client 1.12.1", Loc.F("Play_ClientVersion", "1.12.1"));
    }

    [GeneratedRegex(@"loc:Tr\s+([A-Za-z0-9_]+)|Loc\.[TF]\(\s*""([A-Za-z0-9_]+)""", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
