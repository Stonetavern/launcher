using System.Text.Json;

namespace WowLauncher.Services.AgentControl;

/// <summary>
/// Where a harness learns the port and token: a small file beside the logs.
///
/// <para>A file rather than stdout, because the launcher is a windowed app and its console output is
/// not reliably visible to whoever started it — on macOS it is swallowed entirely when the bundle is
/// opened through <c>open</c>. A file is readable by the same user, from any language, at any time
/// during the run.</para>
///
/// <para><b>🔴 The file IS the credential.</b> On Unix it is created with owner-only permissions and
/// they are re-applied after writing, because <see cref="File.WriteAllText(string,string)"/> on an
/// existing file keeps that file's old mode. It is deleted on shutdown; a stale file from a crashed
/// run points at a port that no longer answers, which fails closed.</para>
/// </summary>
public static class AgentControlHandshake
{
    public const string FileName = "agent-control.json";

    /// <param name="Port">Loopback port the launcher is listening on.</param>
    /// <param name="Token">Value for the <c>X-Agent-Token</c> header.</param>
    /// <param name="Pid">Process id, so a harness can tell a live run from a leftover file.</param>
    public sealed record Contents(int Port, string Token, int Pid);

    /// <summary>Full path of the handshake file inside <paramref name="stateDir"/>.</summary>
    public static string PathIn(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        return System.IO.Path.Combine(stateDir, FileName);
    }

    /// <summary>Writes the file, owner-only where the OS supports it.</summary>
    public static string Write(string stateDir, int port, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        Directory.CreateDirectory(stateDir);
        var path = PathIn(stateDir);

        var json = JsonSerializer.Serialize(
            new Contents(port, token, Environment.ProcessId), AgentControlJson.Options);
        File.WriteAllText(path, json, AgentControlJson.Utf8NoBom);

        RestrictToOwner(path);
        return path;
    }

    /// <summary>Reads the file. Returns null when it is absent or unreadable — both mean "no live
    /// control surface here", which is the safe reading.</summary>
    public static Contents? Read(string stateDir)
    {
        try
        {
            var path = PathIn(stateDir);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Contents>(
                File.ReadAllText(path), AgentControlJson.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Removes the file. Never throws: shutdown must not fail over a leftover.</summary>
    public static void Delete(string stateDir)
    {
        try
        {
            var path = PathIn(stateDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception) { /* best effort */ }
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return; // NTFS inherits the user's profile ACL.

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception) { /* exotic filesystem — the loopback+token guards still hold */ }
    }
}
