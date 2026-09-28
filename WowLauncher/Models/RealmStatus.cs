namespace WowLauncher.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Response from GET https://stonetavern.app/api/realm?realm=&lt;id&gt; (one game realm per call).
///
/// <para>The endpoint documents <c>online</c> as <c>number | null</c>: null means nobody could be
/// counted, which is not the same fact as nobody being online. It used to be an <c>int</c> here, so a
/// null made the whole response fail to parse. <c>presence.online</c> is the database count and the
/// one that stays honest for Barrens, where the web tier cannot reach the world port.</para>
/// </summary>
public sealed class RealmStatusResponse
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "unknown"; // "up" | "down" | "unknown"

    [JsonPropertyName("online")]
    public int? Online { get; set; }

    [JsonPropertyName("presence")]
    public RealmPresence? Presence { get; set; }

    [JsonPropertyName("checkedAt")]
    public long CheckedAt { get; set; }

    /// <summary>The count to show: the database presence when the API sent one, else the top-level
    /// number. Null when neither could be counted.</summary>
    [JsonIgnore]
    public int? Players => Presence is not null ? Presence.Online : Online;
}

public sealed class RealmPresence
{
    [JsonPropertyName("online")]
    public int? Online { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}
