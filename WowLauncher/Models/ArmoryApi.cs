namespace WowLauncher.Models;

using System.Text.Json.Serialization;

// ── Armory contract DTOs (SSOT: /AI/projects/wow/launcher/HANDOFF-armory.md) ─────────────────────
//
// 🔴 UNVERIFIED WIRE SHAPE. As of 2026-07-21 the endpoint these DTOs describe does NOT exist yet:
// the web app serves the armory only as a server-rendered page, and its launcher API surface is
// login + friends + account/online. The shape below is the contract this launcher REQUESTS in
// HANDOFF-armory.md; it has never been checked against a live response.
//
// That distinction is the whole reason this comment exists. The login work on the same day shipped a
// test fixture invented from a spec: `expiresAt` was documented as a string, the API sent a JSON
// number, every test was green and every sign-in was broken. So: when the endpoint goes live, capture
// one REAL response body, drop it into WowLauncher.Tests/fixtures/, and only then treat the mapping
// tests as evidence. Until then HttpArmoryService is dormant (the mock drives the UI) and the DTOs
// are a proposal, not a proof.
//
// Every wire field is camelCase, matching FriendsApi.cs, so the source-gen context below carries a
// CamelCase naming policy and the PascalCase properties line up 1:1. System.Text.Json source
// generation only - no Newtonsoft, trim/AOT-safe.

/// <summary>One saved raid lockout for the current reset. <c>mapName</c> is resolved server side so
/// the launcher binary carries no game data table.</summary>
internal sealed class ArmoryLockoutDto
{
    public int Map { get; set; }
    public string? MapName { get; set; }
    /// <summary>Unix timestamp in SECONDS (the DB column is a unix time, not milliseconds).</summary>
    public long ResetTime { get; set; }
}

/// <summary>One character of the signed-in account. Display names (class, race, zone, rank, tier) are
/// resolved server side; the launcher only formats numbers and picks a class colour from the numeric
/// class id.</summary>
internal sealed class ArmoryCharacterDto
{
    public long Guid { get; set; }
    public string? Name { get; set; }
    public int Race { get; set; }
    public string? RaceName { get; set; }
    public int Class { get; set; }
    public string? ClassName { get; set; }
    public int Level { get; set; }
    public bool Online { get; set; }
    public string? Guild { get; set; }
    public string? GuildRank { get; set; }
    public string? ZoneName { get; set; }
    /// <summary>Money in COPPER (the raw DB value), not gold.</summary>
    public long Money { get; set; }
    /// <summary>Total /played in SECONDS.</summary>
    public long PlayedTimeTotal { get; set; }
    public int QuestCount { get; set; }
    public int HonorRank { get; set; }
    public string? HonorRankName { get; set; }
    /// <summary>Armory score, 0-600, computed server side (the item-level table is 6 MB and stays
    /// on the server - the launcher never computes this).</summary>
    public int Score { get; set; }
    public string? ScoreTier { get; set; }
    public ArmoryLockoutDto[]? Lockouts { get; set; }
}

/// <summary>Body of <c>GET /api/launcher/characters</c>. The realm is echoed back so the launcher can
/// see which realm the server actually resolved, instead of assuming the one it asked for.</summary>
internal sealed class ArmoryResponseDto
{
    public string? Realm { get; set; }
    public ArmoryCharacterDto[]? Characters { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ArmoryResponseDto))]
internal partial class ArmoryApiJsonContext : JsonSerializerContext { }
