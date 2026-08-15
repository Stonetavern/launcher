namespace WowLauncher.Models;

using System.Text.Json.Serialization;

/// <summary>
/// One news entry as served by downloads.stonetavern.app/news.json. The launcher renders these in
/// the news rail (§5); the PatchNotes section filters to <see cref="Category"/> == "patch".
/// </summary>
public sealed class NewsItem
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";          // ISO yyyy-MM-dd; rendered short ("26 JUN")

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    /// <summary>e.g. "patch", "event", "ban-wave", "news" — drives the category tag + PatchNotes filter.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "news";

    /// <summary>Optional external link; click opens it in the default browser (never an in-app webview).</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    // ─── View projections (no logic in XAML) ──────────────────────────────

    /// <summary>
    /// The date as the news rail shows it, e.g. "27 JUL".
    ///
    /// 🔴 InvariantCulture on BOTH calls, and that is not decoration. Until
    /// 2026-08-02 this line hardcoded de-DE, so the rail rendered "27 JULI" to
    /// every player on every machine (German abbreviates July as "Juli", not
    /// "Jul"). The launcher is an English-only surface by decision
    /// ((internal design notes, not published), and see Localization/Loc.cs), so a German
    /// month name was wrong for everybody, not merely wrong abroad.
    ///
    /// Parsing needs the same treatment for the opposite reason: the feed sends
    /// ISO yyyy-MM-dd, and reading it under whatever culture the OS happens to
    /// carry is how a date silently becomes a different date. Neither call may
    /// go back to a named or ambient culture.
    /// </summary>
    [JsonIgnore]
    public string DateShort => System.DateTime.TryParse(
            Date,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var d)
        ? d.ToString("dd MMM", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant()
        : Date.ToUpperInvariant();

    [JsonIgnore]
    public bool IsPatch => string.Equals(Category, "patch", System.StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// Short uppercase tag for the category chip (PATCH / EVENT / BAN-WAVE …).
    ///
    /// The chip used to read "BAN-WELLE", the German word, on the same
    /// English-only surface as the date above. Both spellings are still ACCEPTED
    /// as input, because a hand written news.json on the download host may carry
    /// the old one and dropping it would silently downgrade that entry to NEWS.
    /// Only what a player reads changed.
    /// </summary>
    [JsonIgnore]
    public string CategoryTag => Category.ToUpperInvariant() switch
    {
        "PATCH" => "PATCH",
        "EVENT" => "EVENT",
        "BAN-WAVE" or "BAN-WELLE" => "BAN-WAVE",
        _ => "NEWS",
    };
}

public sealed class NewsFeed
{
    [JsonPropertyName("items")]
    public List<NewsItem> Items { get; set; } = [];
}
