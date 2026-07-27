namespace WowLauncher.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Response from GET https://stonetavern.app/api/realm
/// </summary>
public sealed class RealmStatusResponse
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "unknown"; // "up" | "down"

    [JsonPropertyName("online")]
    public int Online { get; set; }

    [JsonPropertyName("checkedAt")]
    public long CheckedAt { get; set; }
}
