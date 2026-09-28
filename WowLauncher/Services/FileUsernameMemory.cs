namespace WowLauncher.Services;

using System.Text;
using WowLauncher.Services.Platform;

/// <summary>The account name the sign-in forms pre-fill once a player ticked "Remember username".
/// Load returns null when nothing is remembered, which is also the default: the box starts unticked.</summary>
public interface IUsernameMemory
{
    string? Load();
    void Save(string username);
    void Clear();
}

/// <summary>
/// Keeps the remembered name in its own file next to the session (<c>remembered_username.dat</c>),
/// sealed exactly like <see cref="FileTokenStore"/>.
///
/// <para>Not in <c>launcher_config.json</c>: a config gets copied and shared, and an account name
/// identifies a player. Not in the session file either: signing out deletes that, and a remembered
/// name has to outlive a sign-out to be of any use.</para>
///
/// The name is never logged, only the file path on failure.
/// </summary>
public sealed class FileUsernameMemory : IUsernameMemory
{
    private readonly string _path;
    private readonly Serilog.ILogger _log;

    public FileUsernameMemory(IAppPaths paths, Serilog.ILogger log)
    {
        _path = Path.Combine(paths.ConfigDir, "remembered_username.dat");
        _log = log;
    }

    public string? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var name = Encoding.UTF8.GetString(FileTokenStore.Unprotect(File.ReadAllBytes(_path)));
            return name.Length > 0 ? name : null;
        }
        catch (Exception ex)
        {
            // Unreadable or from another machine: forget it, the player types the name once more.
            _log.Warning(ex, "Could not read remembered username at {Path}; forgetting it", _path);
            Clear();
            return null;
        }
    }

    public void Save(string username)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllBytes(_path, FileTokenStore.Protect(Encoding.UTF8.GetBytes(username)));
            FileTokenStore.RestrictPermissions(_path);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not remember username at {Path}", _path);
        }
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { _log.Warning(ex, "Could not forget remembered username at {Path}", _path); }
    }
}
