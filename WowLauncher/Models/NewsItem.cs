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
    [JsonIgnore]
    public string DateShort => System.DateTime.TryParse(Date, out var d)
        ? d.ToString("dd MMM", System.Globalization.CultureInfo.GetCultureInfo("de-DE")).ToUpperInvariant()
        : Date.ToUpperInvariant();

    [JsonIgnore]
    public bool IsPatch => string.Equals(Category, "patch", System.StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    /// <summary>Short uppercase tag for the category chip (PATCH / EVENT / BAN-WELLE …).</summary>
    [JsonIgnore]
    public string CategoryTag => Category.ToUpperInvariant() switch
    {
        "PATCH" => "PATCH",
        "EVENT" => "EVENT",
        "BAN-WAVE" or "BAN-WELLE" => "BAN-WELLE",
        _ => "NEWS",
    };
}

public sealed class NewsFeed
{
    [JsonPropertyName("items")]
    public List<NewsItem> Items { get; set; } = [];
}
