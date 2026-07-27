using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WowLauncher.Localization;

/// <summary>
/// The launcher's UI strings, in one place.
///
/// <para><b>English only, deliberately.</b> The shipped WoW client is English only (see
/// <see cref="Models.ClientLocales"/>), so a translated launcher in front of an English game would be
/// theatre. <c>projects/wow/VOICE.md</c> says the same thing for every player-facing text. This class
/// therefore does no culture handling and no runtime switching - it is a lookup table. Its job is to
/// keep every visible string out of the code and in one reviewable file, which is also what makes a
/// second language cheap later (owner decision 2026-07-21, see
/// <c>/AI/.trash/2026-07-21-launcher-i18n-mehrsprachig/README.md</c> for the removed German catalog
/// and what re-adding one takes).</para>
///
/// <para>The catalog is a flat <c>key -&gt; text</c> JSON embedded in the assembly (same shape as the
/// website's <c>messages/*.json</c>). Embedded rather than a satellite assembly on purpose: satellite
/// resolution inside a <c>PublishSingleFile</c> bundle fails <em>silently</em>, which is exactly the
/// green-but-wrong failure CORE warns about. An unknown key renders as the key itself, so a typo shows
/// up as <c>Play_Cta_Play</c> on screen instead of a blank label.</para>
/// </summary>
public static class Loc
{
    /// <summary>The one shipped language. Also the file name of the catalog.</summary>
    public const string Code = "en";

    private static readonly Dictionary<string, string> Strings = Load() ?? [];

    /// <summary>Look up a key. Unknown keys render as the key itself (loud, not empty).</summary>
    public static string T(string key) =>
        Strings.TryGetValue(key, out var hit) && hit.Length > 0 ? hit : key;

    /// <summary>Formatted lookup: <c>Loc.F("Play_Status_EraTransition", phase)</c>.</summary>
    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(key), args);

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
        Strings.TryGetValue(key, out var previous);
        Strings[key] = value;
        return new Restore(key, previous);
    }

    private sealed class Restore(string key, string? previous) : IDisposable
    {
        public void Dispose()
        {
            if (previous is null) Strings.Remove(key);
            else Strings[key] = previous;
        }
    }

    /// <summary>Read the embedded catalog. Null only when the EmbeddedResource glob broke, which
    /// <c>LocalizationCatalogTests</c> turns into a red test rather than a UI full of raw keys.</summary>
    internal static Dictionary<string, string>? Load()
    {
        var asm = typeof(Loc).Assembly;
        using var stream = asm.GetManifestResourceStream($"WowLauncher.Localization.lang.{Code}.json");
        if (stream is null) return null;
        return JsonSerializer.Deserialize(stream, LangJson.Default.DictionaryStringString);
    }
}

/// <summary>Source-generated context for the catalog (no reflection-based serializer).</summary>
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LangJson : JsonSerializerContext;
