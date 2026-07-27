namespace WowLauncher.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>
/// Stores the launcher session in a single file next to the config (<see cref="IAppPaths.ConfigDir"/>,
/// <c>launcher_session.dat</c>), kept OUT of <c>launcher_config.json</c> so a config that is copied,
/// shared or committed never carries a token.
///
/// <para><b>At-rest protection.</b> On <b>Windows</b> the blob is encrypted with DPAPI
/// (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>) — the same OS-native
/// user-bound key store Windows itself uses for credentials, so the token is unreadable by another
/// user or off this machine, no key management on our side.</para>
///
/// <para><b>Non-Windows (Linux/macOS)</b> has no equivalent zero-config OS key store, so for v1 the
/// blob is written in <b>plaintext</b> with owner-only file permissions (mode <c>600</c>). This is a
/// <b>deliberate, documented v1 compromise</b>: a bearer token in a 0600 file under the user home is
/// no more exposed than the session cookies every browser keeps the same way. A real secret-service
/// backend (libsecret / Keychain) is a later hardening step (see TODO in the handoff return).</para>
///
/// The token is a secret: this class NEVER writes it to a log (only the file path, on failure).
/// </summary>
public sealed class FileTokenStore : ITokenStore
{
    private readonly string _path;
    private readonly Serilog.ILogger _log;

    // DPAPI entropy: a fixed application salt mixed into the Windows user key. Not itself a secret
    // (it ships in the binary); it only scopes the ciphertext to this app so an unrelated DPAPI blob
    // for the same user cannot be swapped in.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("stonetavern-launcher/session/v1");

    public FileTokenStore(IAppPaths paths, Serilog.ILogger log)
    {
        _path = Path.Combine(paths.ConfigDir, "launcher_session.dat");
        _log = log;
    }

    public LauncherSession? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var stored = File.ReadAllBytes(_path);
            if (stored.Length == 0) return null;

            byte[] plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser)
                : stored;

            var json = Encoding.UTF8.GetString(plain);
            return JsonSerializer.Deserialize(json, FriendsApiJsonContext.Default.LauncherSession);
        }
        catch (Exception ex)
        {
            // Corrupt / undecryptable / foreign-machine blob -> treat as signed-out, never throw and
            // never echo the file contents. Drop the unusable file so we start clean next time.
            _log.Warning(ex, "Could not read stored launcher session at {Path} — treating as signed out", _path);
            try { File.Delete(_path); } catch { /* best-effort */ }
            return null;
        }
    }

    public void Save(LauncherSession session)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(session, FriendsApiJsonContext.Default.LauncherSession);
            var plain = Encoding.UTF8.GetBytes(json);

            byte[] toWrite = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
                : plain;

            File.WriteAllBytes(_path, toWrite);
            RestrictPermissions(_path);
            // Path only — the token is a secret and must never reach the log.
            _log.Debug("Stored launcher session at {Path}", _path);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not persist launcher session to {Path}", _path);
        }
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { _log.Warning(ex, "Could not clear launcher session at {Path}", _path); }
    }

    /// <summary>Owner-only (read+write) on POSIX so the plaintext v1 blob is not world/group readable.
    /// No-op on Windows, where DPAPI already binds the ciphertext to the user.</summary>
    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* best-effort — a filesystem without POSIX modes is not a hard failure */ }
    }
}
