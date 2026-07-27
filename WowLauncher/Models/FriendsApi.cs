namespace WowLauncher.Models;

using System.Text.Json.Serialization;

// ── Backend contract DTOs (SSOT: HANDOFF-friends-api.md) ─────────────────────────────────────────
// These mirror the exact on-wire JSON the Next.js web app serves; HttpFriendsPresenceService maps
// them into the UI-facing FriendPresence, and LauncherAuthService into a LauncherSession. Every wire
// field is camelCase, so the source-gen context below carries a CamelCase naming policy and the
// PascalCase property names line up 1:1 (Id->id, ExpiresAt->expiresAt, TargetUsername->targetUsername).
// System.Text.Json source generation only — no Newtonsoft, trim/AOT-safe.

internal sealed class LoginRequestDto
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>Body for <c>POST /api/launcher/register</c>. camelCase on the wire: Terms-&gt;terms,
/// Newsletter-&gt;newsletter, Confirm-&gt;confirm. The server does the authoritative validation and
/// SRP6 credential creation; the launcher only forwards these fields.</summary>
internal sealed class RegisterRequestDto
{
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string Confirm { get; set; } = "";
    public bool Terms { get; set; }
    public bool Newsletter { get; set; }
}

/// <summary>The uniform error envelope every launcher API route returns (<c>{ error, message }</c>).
/// Parsed only to tell the machine <c>error</c> code apart; the message shown to the player is a
/// local VOICE-compliant string chosen by status, never the raw server text (which may carry en
/// dashes from the web validation copy).</summary>
internal sealed class ErrorResponseDto
{
    public string? Error { get; set; }
    public string? Message { get; set; }
}

internal sealed class AccountDto
{
    public long Id { get; set; }
    public string? Username { get; set; }
}

internal sealed class LoginResponseDto
{
    public string? Token { get; set; }
    public AccountDto? Account { get; set; }
    /// <summary>Unix timestamp in MILLISECONDS (the API sends a JSON number, not a string).</summary>
    public long ExpiresAt { get; set; }
}

internal sealed class PresenceDto
{
    public bool Online { get; set; }
    public string? Realm { get; set; }
    public string? Activity { get; set; }
}

/// <summary>One entry of the bare array returned by <c>GET /api/friends</c>. <c>presence</c> is only
/// populated for <c>accepted</c> friends; <c>pending</c> rows arrive offline.</summary>
internal sealed class FriendDto
{
    public long Id { get; set; }
    public AccountDto? Account { get; set; }
    public string? Status { get; set; }       // "accepted" | "pending"
    public string? Direction { get; set; }    // "incoming" | "outgoing"
    public PresenceDto? Presence { get; set; }
}

internal sealed class AddFriendRequestDto
{
    public string TargetUsername { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LoginRequestDto))]
[JsonSerializable(typeof(RegisterRequestDto))]
[JsonSerializable(typeof(ErrorResponseDto))]
[JsonSerializable(typeof(LoginResponseDto))]
[JsonSerializable(typeof(FriendDto[]))]
[JsonSerializable(typeof(AddFriendRequestDto))]
[JsonSerializable(typeof(WowLauncher.Services.LauncherSession))]
internal partial class FriendsApiJsonContext : JsonSerializerContext { }
