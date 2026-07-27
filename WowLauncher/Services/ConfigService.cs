namespace WowLauncher.Services;

using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>
/// Loads and saves launcher configuration from launcher_config.json.
/// Implements fallback-to-defaults on corruption or missing file.
/// </summary>
public interface IConfigService
{
    LauncherConfig Load();
    void Save(LauncherConfig config);

    /// <summary>
    /// False once a <see cref="Save"/> failed to reach disk. The UI must be able to say so: a config
    /// directory the process cannot write (Program Files, restrictive ACLs, full disk) otherwise looks
    /// exactly like a working launcher until the next start throws every setting away.
    /// </summary>
    bool LastSaveSucceeded { get; }
}

public sealed class ConfigService : IConfigService
{
    private readonly string _configPath;
    private readonly string _legacyPath;
    private bool _migrationChecked;
    private bool _saveBlocked;

    /// <inheritdoc/>
    public bool LastSaveSucceeded { get; private set; } = true;
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ConfigService(IAppPaths paths)
    {
        _configPath = paths.ConfigFilePath;
        // The pre-XDG location: next to the exe. On Windows this equals _configPath (ConfigDir is the
        // exe dir) → the migration below is a no-op. On Linux it's the source we migrate from once.
        _legacyPath = Path.Combine(AppContext.BaseDirectory, "launcher_config.json");
    }

    /// <summary>
    /// One-time, Linux-only migration: if no config exists at the XDG location but a launcher_config.json
    /// is sitting next to the binary (the pre-WP3 location), copy it across once so an updating Linux user
    /// keeps their settings. Windows never triggers this (the two paths are identical). Never throws.
    /// </summary>
    private void EnsureMigrated()
    {
        if (_migrationChecked) return;
        _migrationChecked = true;
        try
        {
            if (string.Equals(_configPath, _legacyPath, StringComparison.Ordinal)) return; // Windows / same location
            if (File.Exists(_configPath)) return;                                           // already migrated / native
            if (!File.Exists(_legacyPath)) return;                                          // nothing to migrate

            var dir = Path.GetDirectoryName(_configPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.Copy(_legacyPath, _configPath);
            Serilog.Log.Information("Migrated launcher_config.json from legacy location {From} to {To}", _legacyPath, _configPath);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Config migration from {From} to {To} failed — starting from defaults", _legacyPath, _configPath);
        }
    }

    public LauncherConfig Load()
    {
        EnsureMigrated();
        try
        {
            if (!File.Exists(_configPath))
            {
                var defaults = new LauncherConfig();
                RealmRegistry.ApplyActiveRealm(defaults);
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(_configPath);
            var cfg = System.Text.Json.JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions)
                      ?? new LauncherConfig();

            if (MigrateLegacyServers(cfg)) Save(cfg);

            // Sync the flat connection fields from the selected realm.
            RealmRegistry.ApplyActiveRealm(cfg);
            return cfg;
        }
        catch (System.Text.Json.JsonException ex)
        {
            // The file is there but is not valid JSON (truncated by a power cut, hand-edited, a
            // half-written save). Returning defaults is right, but SILENTLY doing so meant the next
            // save wrote defaults over it and the player lost every realm and every registered client
            // install with no way back. Keep the original before handing out defaults.
            Serilog.Log.Error(ex, "launcher_config.json is not readable JSON — starting from defaults");
            PreserveDamagedConfig("corrupt");
            return Defaults();
        }
        catch (Exception ex)
        {
            // Anything else (permissions, file locked, IO): same rule, the original must survive.
            Serilog.Log.Error(ex, "launcher_config.json could not be loaded — starting from defaults");
            PreserveDamagedConfig("unreadable");
            return Defaults();
        }
    }

    private static LauncherConfig Defaults()
    {
        var defaults = new LauncherConfig();
        RealmRegistry.ApplyActiveRealm(defaults);
        return defaults;
    }

    /// <summary>
    /// Move an unreadable config aside so the defaults that replace it cannot destroy it. The copy is
    /// timestamped, so repeated failures never overwrite the first (and best) surviving version. If the
    /// rename itself fails the file stays where it is and <see cref="Save"/> is blocked instead, which
    /// is the honest outcome: better to lose this session than to overwrite recoverable data.
    /// </summary>
    private void PreserveDamagedConfig(string why)
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            // The stamp only resolves to seconds, and two damaged loads inside one second are not
            // exotic (start the launcher, it fails, the player starts it again). Without the counter the
            // second File.Move hit an existing name, threw, and blocked EVERY save for the rest of the
            // session — a much larger loss than the one this method exists to prevent.
            var stem = $"{_configPath}.{why}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var backup = stem + ".bak";
            for (var n = 2; File.Exists(backup) && n < 1000; n++) backup = $"{stem}-{n}.bak";
            File.Move(_configPath, backup, overwrite: false);
            Serilog.Log.Warning("Kept the damaged config at {Path}", backup);
        }
        catch (Exception ex)
        {
            _saveBlocked = true;
            LastSaveSucceeded = false;
            Serilog.Log.Error(ex, "Could not move the damaged config aside — refusing to overwrite {Path}", _configPath);
        }
    }

    /// <summary>
    /// One-shot migration of the pre-2026-07-21 "server profiles" into <see cref="LauncherConfig.Realms"/>.
    /// A player who added their own server must not lose it just because the concept was renamed, so the
    /// old list is read, converted and then cleared. Returns true when something changed and the config
    /// should be written back.
    /// </summary>
    private static bool MigrateLegacyServers(LauncherConfig cfg)
    {
        if (cfg.CustomServers.Count == 0 && cfg.SelectedServerId.Length == 0) return false;

        var known = cfg.Realms.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in RealmRegistry.Presets().Select(p => p.Id)) known.Add(id);

        var moved = 0;
        foreach (var legacy in cfg.CustomServers)
        {
            if (string.IsNullOrWhiteSpace(legacy.Id) || known.Contains(legacy.Id)) continue;
            cfg.Realms.Add(legacy.ToRealm());
            known.Add(legacy.Id);
            moved++;
        }

        // The old default id was "stonetavern"; it has no direct successor, so that selection lands on
        // the first preset instead of a realm that does not exist.
        if (cfg.SelectedServerId.Length > 0 && known.Contains(cfg.SelectedServerId)
            && !string.Equals(cfg.SelectedServerId, "stonetavern", StringComparison.OrdinalIgnoreCase))
        {
            cfg.SelectedRealmId = cfg.SelectedServerId;
        }

        cfg.CustomServers.Clear();
        cfg.SelectedServerId = "";
        Serilog.Log.Information("Migrated {N} legacy server profile(s) into realms", moved);
        return true;
    }

    public void Save(LauncherConfig config)
    {
        if (_saveBlocked)
        {
            Serilog.Log.Warning("Config save skipped: a damaged {Path} could not be moved aside", _configPath);
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (dir is not null) Directory.CreateDirectory(dir);

            var json = System.Text.Json.JsonSerializer.Serialize(config, JsonOptions);

            // Atomic: write the whole document to a scratch file, flush it to the device, then move it
            // over the real one. File.WriteAllText truncates first and writes second, so a power cut or
            // a kill in between left a half-written file that the next start read as corrupt. The
            // download path already worked this way; the config path did not.
            // Process-unique scratch name: two launcher instances sharing one config directory used to
            // collide on the single "<config>.tmp" with FileShare.None, and one of them lost its
            // settings for the session.
            var tmp = $"{_configPath}.{Environment.ProcessId}.tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, _configPath, overwrite: true);
            LastSaveSucceeded = true;
        }
        catch (Exception ex)
        {
            LastSaveSucceeded = false;
            Serilog.Log.Error(ex, "Failed to save config to {Path}", _configPath);
        }
    }
}
