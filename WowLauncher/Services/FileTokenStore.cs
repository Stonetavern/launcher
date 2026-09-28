namespace WowLauncher.Services;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// <para><b>Non-Windows (Linux/macOS)</b> seals the blob with AES-256-GCM under a key derived
/// (SHA-256) from the machine id, the user name and a fixed application salt. The machine id is
/// <c>/etc/machine-id</c> on Linux and the <c>IOPlatformUUID</c> on macOS; the machine name is the
/// documented fallback. A copied file therefore fails the GCM tag check on a different machine or for
/// a different user and is treated as signed out — the token never sits on disk in cleartext.</para>
///
/// <para>The file keeps owner-only permissions (mode <c>600</c>) as a second layer, because the
/// machine-id derivation is weaker than a real OS key store. A libsecret/Keychain backend remains a
/// later hardening step; §12.2 chose "token only, machine-bound", not a full keychain port.</para>
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

    // Container of the non-Windows blob: magic || nonce(12) || tag(16) || ciphertext.
    private static readonly byte[] Magic = "STLS1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

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

            var plain = Unprotect(stored);

            var json = Encoding.UTF8.GetString(plain);
            return JsonSerializer.Deserialize(json, FriendsApiJsonContext.Default.LauncherSession);
        }
        catch (Exception ex)
        {
            // Corrupt / undecryptable / foreign-machine blob (or a pre-1.9 plaintext file) -> treat as
            // signed-out, never throw and never echo the file contents. Drop the unusable file so we
            // start clean next time.
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

            var toWrite = Protect(plain);

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

    /// <summary>The at-rest sealing described above, shared with <see cref="FileUsernameMemory"/> so
    /// the launcher has one policy for what it keeps about an account, not two.</summary>
    internal static byte[] Protect(byte[] plain) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
        : Seal(plain);

    /// <summary>Throws when the blob is not ours, is from another machine or user, or was tampered with.</summary>
    internal static byte[] Unprotect(byte[] stored) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser)
        : Unseal(stored);

    /// <summary>Owner-only (read+write) on POSIX so the sealed blob is not world/group readable.
    /// No-op on Windows, where DPAPI already binds the ciphertext to the user.</summary>
    internal static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* best-effort — a filesystem without POSIX modes is not a hard failure */ }
    }

    // ── Non-Windows sealing (AES-256-GCM, key bound to machine + user) ──────────────────────────

    private static byte[] Seal(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(MachineKey(), TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[Magic.Length + NonceSize + TagSize + cipher.Length];
        Magic.CopyTo(blob, 0);
        nonce.CopyTo(blob, Magic.Length);
        tag.CopyTo(blob, Magic.Length + NonceSize);
        cipher.CopyTo(blob, Magic.Length + NonceSize + TagSize);
        return blob;
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the blob is not ours, was sealed on
    /// another machine/user, or was tampered with — the caller's catch treats all three as signed out.</summary>
    private static byte[] Unseal(byte[] stored)
    {
        if (stored.Length < Magic.Length + NonceSize + TagSize
            || !stored.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Unrecognized launcher session format");

        var nonce = stored.AsSpan(Magic.Length, NonceSize);
        var tag = stored.AsSpan(Magic.Length + NonceSize, TagSize);
        var cipher = stored.AsSpan(Magic.Length + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(MachineKey(), TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    /// <summary>Key = SHA-256 over application salt + machine id + user. Not a secret (all inputs are
    /// local and readable), but it binds the ciphertext to THIS machine and THIS user: a copied file
    /// fails the GCM tag check and is treated as signed out.</summary>
    private static byte[] MachineKey() =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"stonetavern-launcher/session/v1\n{MachineId()}\n{Environment.UserName}"));

    private static string MachineId()
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                try
                {
                    var id = File.ReadAllText(path).Trim();
                    if (id.Length > 0) return id;
                }
                catch { /* try the next source */ }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            // The macOS analogue of /etc/machine-id is the hardware UUID. ioreg prints it; any
            // failure falls through to the machine name, which is still a stable local binding.
            try
            {
                using var p = Process.Start(new ProcessStartInfo("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                })!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                var m = Regex.Match(output, "\"IOPlatformUUID\"\\s*=\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
            catch { /* fall through */ }
        }
        return Environment.MachineName;
    }
}
