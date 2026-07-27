namespace WowLauncher.Services;

/// <summary>The persisted launcher session: the bearer token plus which account it belongs to. Held
/// only while signed in; cleared on sign-out. The <see cref="Token"/> is a secret and is NEVER
/// logged.</summary>
/// <remarks><paramref name="ExpiresAt"/> is a Unix timestamp in MILLISECONDS, matching what the web
/// API sends (<c>Date.now() + TTL</c> in lib/launcher-token.ts). 0 means "unknown" and is treated as
/// not-expired: the server is the real gate and 401s a dead token.</remarks>
public sealed record LauncherSession(string Token, long AccountId, string Username, long ExpiresAt);

/// <summary>
/// Persists the signed-in <see cref="LauncherSession"/> across launches so a returning player is
/// already logged in. One seam so the storage policy (encrypt-at-rest vs restrictive file mode) lives
/// in exactly one place. Load returns null when nothing is stored or the stored blob cannot be read.
/// </summary>
public interface ITokenStore
{
    LauncherSession? Load();
    void Save(LauncherSession session);
    void Clear();
}
