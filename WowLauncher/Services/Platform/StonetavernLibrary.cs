using System.Text.Json;

namespace WowLauncher.Services.Platform;

/// <summary>
/// The one Stonetavern folder (owner 2026-09-28: "like Blizzard, one folder, both clients in it").
/// A new player picks it once, on the first start, and gets:
/// <code>
/// Stonetavern/
///   Launcher/          the launcher itself (Windows: exe + DLLs + tools; Linux: the AppImage)
///   Classic-1.12.1/    the 1.12.1 package, content unchanged
///   Modern-1.14.2/     the 1.14.2 package, content unchanged
/// </code>
/// No realm level: one client serves every realm, the realmlist is written at launch.
///
/// <para>🔴 <b>Existing players are never moved.</b> Anything that shows a player has used the launcher
/// before (a config in any form, a client folder the launcher made) keeps the old code path byte for
/// byte. Concept + second opinion: <c>KONZEPT-2026-09-28-stonetavern-ordner.md</c>,
/// <c>CODEX-VERDICT-KONZEPT-2026-09-28-stonetavern-ordner.md</c>.</para>
/// </summary>
public static class LibraryNames
{
    /// <summary>The launcher's own folder inside the library.</summary>
    public const string LauncherDir = "Launcher";

    /// <summary>Written last by a completed setup; its absence means "not set up here".</summary>
    public const string MarkerFile = ".stonetavern-library.json";

    /// <summary>Transient setup state: the lock and the handoff markers.</summary>
    public const string SetupDir = ".stonetavern-setup";

    /// <summary>Inside a launcher folder this setup created and has not finished: safe to replace.</summary>
    public const string OwnedMarker = ".stonetavern-owned";

    /// <summary>The fixed name of the AppImage inside the library. The download carries the version in
    /// its name, which then stays wrong forever after the first self-update.</summary>
    public const string AppImageName = "stonetavern-launcher.AppImage";

    /// <summary>The Windows release lists every file it ships here (relative path + SHA-256), so the
    /// setup copies the complete bundle and can prove it did.</summary>
    public const string PayloadList = "PAYLOAD.sha256";

    /// <summary>The package folder of a build inside the library. The package content (and with it
    /// every path the patcher knows) is unchanged; only the folder it sits in has a readable name.</summary>
    public static string PackageDir(int build) => build switch
    {
        5875 => "Classic-1.12.1",
        42597 => "Modern-1.14.2",
        _ => $"Client-{build}",
    };

    /// <summary>The folder name the launcher used before the library (still used for every existing
    /// player). Its presence anywhere we look is evidence of an existing player.</summary>
    public static bool IsLegacyClientDirName(string name) =>
        name.StartsWith("WoW-Client-", StringComparison.OrdinalIgnoreCase)
        && name.Length > "WoW-Client-".Length
        && name["WoW-Client-".Length..].All(char.IsAsciiDigit);

    /// <summary>The suggested place: <c>~/Games/Stonetavern</c> on every OS (Windows: the user
    /// profile). Writable without admin, not synced by OneDrive by default, not a TCC-protected folder
    /// on macOS, and where Lutris/Heroic keep games on Linux.</summary>
    public static string DefaultRoot(string home) => Path.Combine(home, "Games", "Stonetavern");
}

/// <summary>What <c>.stonetavern-library.json</c> says. <see cref="LauncherDir"/> is null where the
/// launcher does not live inside the library (macOS keeps its .app; a build without a payload list).</summary>
public sealed record LibraryMarker(int Schema, string Root, string? LauncherDir, DateTimeOffset CompletedAt)
{
    public static LibraryMarker? TryRead(string root)
    {
        try
        {
            var path = Path.Combine(root, LibraryNames.MarkerFile);
            if (!File.Exists(path)) return null;
            var marker = JsonSerializer.Deserialize(File.ReadAllText(path), LibraryJson.Default.LibraryMarker);
            return marker is { Schema: >= 1 } && !string.IsNullOrWhiteSpace(marker.Root) ? marker : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Atomic: a torn marker would read as "set up" with half a path.</summary>
    public void Write()
    {
        var path = Path.Combine(Root, LibraryNames.MarkerFile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, LibraryJson.Default.LibraryMarker));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>A setup the original launcher started and the copied launcher finishes.</summary>
public sealed record LibraryHandoff(string Root, string Nonce, string LauncherDir, string OriginPath);

[System.Text.Json.Serialization.JsonSerializable(typeof(LibraryMarker))]
[System.Text.Json.Serialization.JsonSerializable(typeof(LibraryHandoff))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class LibraryJson : System.Text.Json.Serialization.JsonSerializerContext;

public enum LibraryDecisionKind
{
    /// <summary>An existing player: the launcher behaves exactly as before the library existed.</summary>
    Legacy,

    /// <summary>A player who set up the library; this is the launcher in it.</summary>
    Library,

    /// <summary>A stray copy (the old download) of a player who set up the library: start the one in
    /// the library and quit.</summary>
    Forward,

    /// <summary>Nothing shows this player has been here: ask where Stonetavern should live.</summary>
    Setup,

    /// <summary>This process was started by a setup to finish it.</summary>
    Handoff,
}

public sealed record LibraryDecision(LibraryDecisionKind Kind, string? Root = null, string? Target = null,
                                     string? Nonce = null, string Reason = "");

/// <summary>Everything the decision needs, read from disk beforehand, so the decision is pure.</summary>
/// <param name="ConfigEvidence">Any launcher config exists: the primary file, its <c>.good</c> copy, a
/// preserved damaged copy, or the pre-XDG file next to the binary.</param>
/// <param name="ConfigLibraryRoot">The <c>LibraryRoot</c> of the primary config, when it has one.</param>
/// <param name="LegacyClientFolder">A <c>WoW-Client-&lt;build&gt;</c> folder in a place the launcher
/// put clients or a player would have picked. The first one found, for the log.</param>
/// <param name="SelfPath">The durable path of this launcher: <c>$APPIMAGE</c> on Linux, the exe on
/// Windows. Null in a dev run.</param>
/// <param name="ForwardTarget">The launcher inside a completed library, when one is known and exists
/// (Windows: from the HKCU pointer; Linux: from the config). Null otherwise.</param>
public sealed record LibraryFacts(
    string? HandoffRoot,
    string? HandoffNonce,
    bool ConfigEvidence,
    string? ConfigLibraryRoot,
    string? LegacyClientFolder,
    string? SelfPath,
    string? ForwardTarget,
    bool PathsIgnoreCase);

public static class LibraryClassifier
{
    /// <summary>
    /// The one rule (Codex Terra 2026-09-28, conservative on purpose): an existing player must never
    /// be taken for a new one. A new player taken for an existing one only keeps the old download
    /// flow, which is what every player had until now.
    /// </summary>
    public static LibraryDecision Decide(LibraryFacts f)
    {
        if (!string.IsNullOrWhiteSpace(f.HandoffRoot) && !string.IsNullOrWhiteSpace(f.HandoffNonce))
            return new(LibraryDecisionKind.Handoff, f.HandoffRoot, Nonce: f.HandoffNonce, Reason: "started by setup");

        if (!string.IsNullOrWhiteSpace(f.ConfigLibraryRoot))
        {
            // Linux: the config is shared by every copy of the AppImage, so an old download started
            // again reads the library config too. It must hand over to the copy in the library, or it
            // would run (and self-update) a second launcher beside it.
            if (IsOtherLauncher(f))
                return new(LibraryDecisionKind.Forward, f.ConfigLibraryRoot, f.ForwardTarget, Reason: "library launcher exists");
            return new(LibraryDecisionKind.Library, f.ConfigLibraryRoot, Reason: "config names the library");
        }

        if (f.ConfigEvidence)
            return new(LibraryDecisionKind.Legacy, Reason: "a launcher config exists");

        // Windows: the old download has no config of its own (that lives next to the library exe).
        // Only here, where it would otherwise be asked to set up again, does the pointer count.
        if (IsOtherLauncher(f))
            return new(LibraryDecisionKind.Forward, Target: f.ForwardTarget, Reason: "library launcher exists");

        if (f.LegacyClientFolder is { } folder)
            return new(LibraryDecisionKind.Legacy, Reason: $"client folder {folder}");

        return new(LibraryDecisionKind.Setup, Reason: "no sign of an earlier install");
    }

    private static bool IsOtherLauncher(LibraryFacts f) =>
        f.ForwardTarget is { } target && f.SelfPath is { } self
        && !SamePath(self, target, f.PathsIgnoreCase);

    internal static bool SamePath(string a, string b, bool ignoreCase)
    {
        try
        {
            a = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            b = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return false;
        }
        return string.Equals(a, b, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// <summary>
/// Reads <see cref="LibraryFacts"/> from the machine. 🔴 Read-only: this runs before the config service
/// exists, precisely because that service writes a default config when there is none, which would wipe
/// the evidence this probe looks for.
/// </summary>
public static class LibraryProbe
{
    public const string HandoffArg = "--library-handoff";
    public const string RootArg = "--library-root";

    public static LibraryFacts Read(IAppPaths paths, string[] args, Func<string?>? windowsPointer = null)
    {
        var home = HomeDir();
        var (handoffRoot, nonce) = HandoffFrom(args);
        var (evidence, libraryRoot) = ReadConfig(paths);
        var self = SelfPath();

        string? forward = null;
        if (libraryRoot is not null)
            forward = LauncherIn(libraryRoot);
        else if (OperatingSystem.IsWindows() && (windowsPointer ?? WindowsLibraryPointer.Read)() is { } pointerRoot)
            forward = LauncherIn(pointerRoot);

        return new LibraryFacts(
            handoffRoot, nonce, evidence, libraryRoot,
            evidence ? null : FindLegacyClientFolder(LegacyParents(paths, home)),
            self, forward, PathsIgnoreCase: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
    }

    internal static (string? Root, string? Nonce) HandoffFrom(string[] args)
    {
        string? After(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        return (After(RootArg), After(HandoffArg));
    }

    /// <summary>Any config file counts as evidence, readable or not (a damaged config is still a player
    /// who was here). The library root comes only from a primary config that parses.</summary>
    internal static (bool Evidence, string? LibraryRoot) ReadConfig(IAppPaths paths)
    {
        var evidence = false;
        string? root = null;
        try
        {
            var dir = Path.GetDirectoryName(paths.ConfigFilePath);
            var name = Path.GetFileName(paths.ConfigFilePath);
            if (dir is not null && Directory.Exists(dir))
                evidence = Directory.EnumerateFiles(dir, name + "*").Any();
            // The pre-XDG location next to the binary (ConfigService migrates it once).
            var legacy = Path.Combine(AppContext.BaseDirectory, name);
            evidence |= File.Exists(legacy);

            if (File.Exists(paths.ConfigFilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(paths.ConfigFilePath));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(p.Name, "LibraryRoot", StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                        root = p.Value.GetString();
                }
            }
        }
        catch (Exception)
        {
            // Unreadable counts as present: never mistake a player with a broken config for a new one.
            evidence = true;
        }
        return (evidence, root);
    }

    /// <summary>The launcher in a completed library, or null: the marker must exist and name a
    /// launcher folder, and the launcher file in it must exist.</summary>
    public static string? LauncherIn(string root)
    {
        if (LibraryMarker.TryRead(root) is not { LauncherDir: { } dir }) return null;
        var file = OperatingSystem.IsWindows()
            ? Path.Combine(dir, Path.GetFileName(Environment.ProcessPath ?? "WowLauncher.exe"))
            : Path.Combine(dir, LibraryNames.AppImageName);
        return File.Exists(file) ? file : null;
    }

    internal static string? SelfPath()
    {
        if (OperatingSystem.IsLinux())
            return Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : null;
        return OperatingSystem.IsWindows() ? Environment.ProcessPath : null;
    }

    /// <summary>Where a <c>WoW-Client-&lt;build&gt;</c> folder of an existing player can sit: the launcher
    /// defaults (next to the exe on Windows, the XDG data dir elsewhere) and the places players picked
    /// in the folder dialog (the owner's own is the Desktop).</summary>
    internal static IEnumerable<string> LegacyParents(IAppPaths paths, string home)
    {
        yield return paths.ShareDir;
        yield return AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(home)) yield break;
        yield return home;
        foreach (var sub in new[] { "Desktop", "Downloads", "Documents", "Games", "Applications" })
            yield return Path.Combine(home, sub);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop)) yield return desktop;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(docs)) yield return docs;
        if (OperatingSystem.IsWindows())
            foreach (var drive in new[] { "C:", "D:", "E:" })
            {
                yield return drive + @"\";
                yield return drive + @"\Games";
            }
    }

    internal static string? FindLegacyClientFolder(IEnumerable<string> parents)
    {
        foreach (var parent in parents.Distinct())
        {
            try
            {
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) continue;
                foreach (var dir in Directory.EnumerateDirectories(parent, "WoW-Client-*"))
                    if (LibraryNames.IsLegacyClientDirName(Path.GetFileName(dir)))
                        return dir;
            }
            catch (Exception)
            {
                // An unreadable folder says nothing either way.
            }
        }
        return null;
    }

    internal static string HomeDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) ? home : Environment.GetEnvironmentVariable("HOME") ?? "";
    }
}

/// <summary>
/// Windows: <c>HKCU\Software\Stonetavern\Launcher\LibraryRoot</c>. Per user, no admin. A hint, never
/// authority: it only counts together with a valid marker and an existing launcher file
/// (<see cref="LibraryProbe.LauncherIn"/>).
/// </summary>
public static class WindowsLibraryPointer
{
    private const string Key = @"Software\Stonetavern\Launcher";
    private const string Value = "LibraryRoot";

    public static string? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Value) is string s && Path.IsPathRooted(s) ? s : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Write(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(Value, root);
    }
}
