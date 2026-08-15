using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WowLauncher.Localization;

/// <summary>
/// The launcher's UI strings, in one place.
///
/// <para><b>The languages the realm can answer in, and no others.</b> Until 2026-08-03 this was
/// English only, and for a good reason at the time: the shipped client was English only, so a
/// translated launcher in front of an English game would have been theatre. That reason is gone —
/// 1.12.1 now installs German, French and Spanish packs and 1.14.2 carries ten languages — so the
/// launcher speaks what the realm speaks (owner decision 2026-08-03). It is deliberately NOT every
/// language a catalog could be written for: offering a language the game then cannot deliver is the
/// half-translated experience this whole area exists to avoid.</para>
///
/// <para><b>Per-key fallback, never per-file.</b> A missing key falls back to English, not to the key
/// name. A catalog that is a few entries behind therefore shows English in those places and stays
/// usable, instead of showing <c>Play_Cta_Play</c> to a player. A key missing from English too still
/// renders as itself — loud rather than blank, so a typo is visible on screen.</para>
///
/// <para>The catalogs are flat <c>key -&gt; text</c> JSON embedded in the assembly (same shape as the
/// website's <c>messages/*.json</c>). Embedded rather than satellite assemblies on purpose: satellite
/// resolution inside a <c>PublishSingleFile</c> bundle fails <em>silently</em>, which is exactly the
/// green-but-wrong failure CORE warns about.</para>
/// </summary>
public static class Loc
{
    /// <summary>The language every catalog falls back to, key by key. Always present.</summary>
    public const string Code = "en";

    /// <summary>
    /// The UI languages that ship, keyed by the client locale they belong to. The launcher does not
    /// get its own language picker: it follows the language the player picked for the GAME, because
    /// two controls for one intent is one more than anybody wants — and a player who set the game to
    /// German did not mean "but keep the launcher English".
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ForClientLocale =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enUS"] = "en",
            ["deDE"] = "de",
            ["frFR"] = "fr",
            ["esES"] = "es",
            ["esMX"] = "es",
            ["ruRU"] = "ru",
        };

    private static readonly Dictionary<string, string> Fallback = Load(Code) ?? [];
    private static Dictionary<string, string> _strings = Fallback;

    /// <summary>The catalog currently in use. Never empty: an unknown or unshipped code leaves
    /// English in place rather than emptying the interface.</summary>
    public static string Current { get; private set; } = Code;

    /// <summary>Raised after <see cref="Use"/> actually changed the language, so the surface can
    /// re-resolve every string it is showing.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Switch the interface language. <paramref name="code"/> is a UI code (<c>de</c>) or a client
    /// locale (<c>deDE</c>) — both are accepted, because the two callers that have an opinion about
    /// language hold it in different shapes.
    /// </summary>
    /// <returns>The language actually in use afterwards.</returns>
    public static string Use(string? code)
    {
        var wanted = Normalise(code);
        if (wanted == Current) return Current;

        var catalog = wanted == Code ? Fallback : Load(wanted);
        if (catalog is null || catalog.Count == 0)
        {
            // A language we do not ship is not an error and not a reason to blank the interface.
            return Current;
        }

        _strings = catalog;
        Current = wanted;
        Catalog.Reload();
        Changed?.Invoke();
        return Current;
    }

    /// <summary>Turn either shape of language identifier into a shipped UI code, or English.</summary>
    internal static string Normalise(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Code;
        var trimmed = code.Trim();
        if (ForClientLocale.TryGetValue(trimmed, out var mapped)) return mapped;
        return ForClientLocale.Values.Contains(trimmed, StringComparer.Ordinal) ? trimmed : Code;
    }

    /// <summary>Look up a key: current language, then English, then the key itself (loud, not
    /// empty).</summary>
    public static string T(string key)
    {
        if (_strings.TryGetValue(key, out var hit) && hit.Length > 0) return hit;
        if (Fallback.TryGetValue(key, out var english) && english.Length > 0) return english;
        return key;
    }

    /// <summary>Formatted lookup: <c>Loc.F("Play_Status_EraTransition", phase)</c>.</summary>
    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(key), args);

    /// <summary>
    /// The catalog as a bindable object, so AXAML can hold a LIVE reference to a string instead of a
    /// copy taken at load time (see <see cref="TrExtension"/>). One instance, one notification on a
    /// switch — the alternative is every view rebuilding itself, which loses scroll position and
    /// selection for a language change.
    /// </summary>
    public sealed class CatalogView : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string this[string key] => T(key);

        /// <summary>
        /// Tell every binding that every entry may have changed.
        ///
        /// <para>Raised TWICE on purpose. <c>"Item[]"</c> is the indexer convention, and an empty name
        /// is the INotifyPropertyChanged convention for "all properties". Only the second one actually
        /// reached the bindings: with <c>"Item[]"</c> alone the launcher switched half way - the strings
        /// a view model computes turned German while every <c>{loc:Tr}</c> label stayed English, which
        /// is visible only in the rendered window and looks like a broken translation rather than a
        /// broken notification (seen in the Linux end-to-end run, 2026-08-04).</para>
        /// </summary>
        internal void Reload()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    /// <summary>The single bindable catalog instance every <c>{loc:Tr}</c> binds against.</summary>
    public static CatalogView Catalog { get; } = new();

    /// <summary>
    /// Test seam: reword one catalog entry for the duration of the returned scope.
    ///
    /// <para>It exists because "this text comes from the catalog" cannot otherwise be proven. A test
    /// that compares a rendered string against <c>Loc.T(key)</c> stays green when the code under test
    /// hardcodes the very same sentence, so it proves nothing and would be a placebo. Rewording the
    /// entry and demanding that the output follows is the only assertion that goes red on a literal.
    /// Not public, not thread safe, and never called by the app.</para>
    /// </summary>
    internal static IDisposable OverrideForTests(string key, string value)
    {
        _strings.TryGetValue(key, out var previous);
        _strings[key] = value;
        return new Restore(key, previous);
    }

    private sealed class Restore(string key, string? previous) : IDisposable
    {
        public void Dispose()
        {
            if (previous is null) _strings.Remove(key);
            else _strings[key] = previous;
        }
    }

    /// <summary>Read one embedded catalog. Null when the language does not ship (or, for English, when
    /// the EmbeddedResource glob broke — which <c>LocalizationCatalogTests</c> turns into a red test
    /// rather than a UI full of raw keys).</summary>
    internal static Dictionary<string, string>? Load(string code)
    {
        var asm = typeof(Loc).Assembly;
        using var stream = asm.GetManifestResourceStream($"WowLauncher.Localization.lang.{code}.json");
        if (stream is null) return null;
        return JsonSerializer.Deserialize(stream, LangJson.Default.DictionaryStringString);
    }

    /// <summary>Every UI language that is actually embedded in this build. Read from the assembly, not
    /// from a list somebody has to keep in step with the files.</summary>
    internal static IReadOnlyList<string> Shipped =>
        typeof(Loc).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("WowLauncher.Localization.lang.", StringComparison.Ordinal)
                        && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n["WowLauncher.Localization.lang.".Length..^".json".Length])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}

/// <summary>Source-generated context for the catalog (no reflection-based serializer).</summary>
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LangJson : JsonSerializerContext;
