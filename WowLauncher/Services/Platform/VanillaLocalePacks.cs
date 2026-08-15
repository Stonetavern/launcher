namespace WowLauncher.Services.Platform;

using WowLauncher.Services;

/// <summary>What a language switch did, and why it did not. Never an exception: a language is a
/// preference, and losing a launch over one would be the wrong trade — the caller falls back to the
/// locale that IS active and starts the game.</summary>
public sealed record VanillaLocaleResult(bool Ok, string Locale, string? Error = null)
{
    public static VanillaLocaleResult Success(string locale) => new(true, locale);
    public static VanillaLocaleResult Failed(string locale, string error) => new(false, locale, error);
}

/// <summary>
/// The language switch for the 1.12.1 client (build 5875).
///
/// <para><b>Why this is not just a config key.</b> The 1.12.1 client does NOT load
/// <c>Data/&lt;loc&gt;/locale-&lt;loc&gt;.MPQ</c> — that is the convention of later versions, and it is
/// what <see cref="ClientService.ConfigureClient"/> assumes. Build 5875 loads, out of <c>Data/</c>,
/// everything matching <c>patch-?.MPQ</c> — exactly ONE character. So a language pack that sits in its
/// locale folder is loaded by nothing, no matter what <c>Config.wtf</c> says, and the player gets an
/// English client and a launcher that reported success. Activating a language means putting the pack
/// into the patch slot the client actually reads.</para>
///
/// <para><b>Why renaming and not copying or linking.</b> A pack is ~90 MB. Copying it means the switch
/// takes seconds and the disk carries every language twice. A symlink needs Developer Mode or elevation
/// on Windows. A rename is instant on every filesystem, needs no rights, and moves no bytes — the same
/// trade <see cref="AddonProfileService"/> makes for addon sets. The active pack therefore LIVES in the
/// slot (<c>Data/patch-Z.MPQ</c>) and goes back to its locale folder when another language takes over.
/// <c>Z</c> is the highest slot and wins alphabetically over everything the packages ship, including
/// the Cinematic variant's HD textures in <c>patch-B</c>/<c>patch-D</c>.</para>
///
/// <para><b>What is never touched.</b> A <c>patch-Z.MPQ</c> without our marker beside it was not put
/// there by this launcher. It is refused rather than moved aside: it is the player's, and the cost of
/// being wrong is a client that no longer starts and a mod they cannot get back.</para>
///
/// <para>Pack layout and the measurements behind it (DBC slot fix, column counts, error #121):
/// <c>/mnt/data/wow/dist/lang-packs-1.12.1/README.md</c>.</para>
/// </summary>
public sealed class VanillaLocalePacks
{
    /// <summary>The patch slot an active language pack occupies, relative to <c>Data/</c>. Highest
    /// letter on purpose — the client loads <c>patch-?.MPQ</c> in alphabetical order and the last one
    /// read wins.</summary>
    public const string SlotFileName = "patch-Z.MPQ";

    /// <summary>Written beside the slot, naming the locale currently in it. Without it a switch cannot
    /// tell its own pack from a file the player put there, and "put it back where it came from" has no
    /// answer.</summary>
    public const string MarkerName = ".stonetavern-locale";

    /// <summary>The locale every 1.12.1 install carries in its base MPQs, and the state the client is
    /// in whenever no pack is active.</summary>
    public const string BaseLocale = "enUS";

    /// <summary>
    /// Written into <c>Data/&lt;loc&gt;/</c>, naming the checksum of the archive this pack came out of.
    ///
    /// <para><b>Why a language pack needs one at all.</b> A pack is installed once and then never looked
    /// at again: the switch asks "is the file there" and, if it is, does not download. That is right for
    /// a switch and wrong for a fix. Ship a corrected deDE pack and every player who already has deDE
    /// keeps the old one forever, with no error and nothing on screen - the launcher reports the
    /// language as installed, and it is, just not the one we published.</para>
    ///
    /// <para>The checksum of the ARCHIVE, not of the unpacked file: it is the identity the manifest
    /// carries, so the comparison is against something we publish rather than something we would have
    /// to compute over 90 MB on every start.</para>
    ///
    /// <para>Lives beside the locale folder rather than beside the slot, because the pack itself moves:
    /// the active language sits in <see cref="SlotFileName"/> and only the folder stays put.</para>
    /// </summary>
    public const string PackOriginName = ".stonetavern-pack";

    private static string PackOriginPath(string clientDir, string locale) =>
        Path.Combine(DataDir(clientDir), locale, PackOriginName);

    /// <summary>Which published archive this installed pack came from, or null when nobody wrote it
    /// down. Null is a THIRD state, not a fourth spelling of "outdated": a pack installed before this
    /// marker existed is probably fine, and telling that player to re-download 90 MB on a guess would
    /// cost them real bandwidth for a maybe.</summary>
    public string? PackOrigin(string clientDir, string locale)
    {
        try
        {
            var path = PackOriginPath(clientDir, locale);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not read the pack origin for {Locale} in {Dir}", locale, clientDir);
            return null;
        }
    }

    /// <summary>
    /// Record which archive this pack came from. Read back before it counts, for the same reason the
    /// addon set marker is: a write that reports success without holding turns "your language is
    /// current" into a claim nobody checked.
    /// </summary>
    public bool WritePackOrigin(string clientDir, string locale, string sha256)
    {
        var wanted = (sha256 ?? "").Trim();
        if (!IsLocaleCode(locale) || wanted.Length == 0) return false;

        try
        {
            var path = PackOriginPath(clientDir, locale);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, wanted);
            var readBack = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            return string.Equals(readBack, wanted, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not record the pack origin for {Locale} in {Dir}", locale, clientDir);
            return false;
        }
    }

    private readonly Serilog.ILogger _log;

    public VanillaLocalePacks(Serilog.ILogger log) => _log = log.ForContext<VanillaLocalePacks>();

    private static string DataDir(string clientDir) => Path.Combine(clientDir, "Data");
    private static string SlotPath(string clientDir) => Path.Combine(DataDir(clientDir), SlotFileName);
    private static string MarkerPath(string clientDir) => Path.Combine(DataDir(clientDir), MarkerName);

    /// <summary>Where a pack lives while it is NOT active. Same layout the ZIPs extract to, so a pack
    /// that was just unpacked is found without any bookkeeping.</summary>
    public static string PackPath(string clientDir, string locale) =>
        Path.Combine(DataDir(clientDir), locale, $"locale-{locale}.MPQ");

    /// <summary>A locale code is a directory name and a config value. Only the exact <c>xxYY</c> shape
    /// passes; anything else is refused rather than repaired, because a repaired locale would name a
    /// path that is not the one the caller meant.</summary>
    internal static bool IsLocaleCode(string? code) =>
        code is { Length: 4 }
        && char.IsAsciiLetterLower(code[0]) && char.IsAsciiLetterLower(code[1])
        && char.IsAsciiLetterUpper(code[2]) && char.IsAsciiLetterUpper(code[3]);

    /// <summary>The locale the client will actually start in: whatever the marker names, or
    /// <see cref="BaseLocale"/> when no pack is active. A marker whose pack has vanished still reads as
    /// that locale here — <see cref="Apply"/> is what reconciles the two.</summary>
    public string Active(string clientDir)
    {
        try
        {
            var marker = MarkerPath(clientDir);
            if (!File.Exists(marker)) return BaseLocale;
            var name = File.ReadAllText(marker).Trim();
            return IsLocaleCode(name) ? name : BaseLocale;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not read the language marker in {Dir}", clientDir);
            return BaseLocale;
        }
    }

    /// <summary>Every language this installation can be switched to right now, without a download:
    /// the packs sitting in their locale folders, the one currently in the slot, and English, which is
    /// in the base MPQs and therefore always available.</summary>
    public IReadOnlyList<string> Installed(string clientDir)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal) { BaseLocale };
        try
        {
            var active = Active(clientDir);
            if (active != BaseLocale && File.Exists(SlotPath(clientDir))) found.Add(active);

            var data = DataDir(clientDir);
            if (Directory.Exists(data))
            {
                foreach (var dir in Directory.EnumerateDirectories(data))
                {
                    var name = Path.GetFileName(dir);
                    if (IsLocaleCode(name) && File.Exists(PackPath(clientDir, name))) found.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not list the installed language packs in {Dir}", clientDir);
        }
        return found.ToList();
    }

    /// <summary>
    /// Make <paramref name="locale"/> the language this installation starts in: move the right pack
    /// into the patch slot, write the three config keys the client reads, and drop the cache that would
    /// otherwise keep serving names in the old language.
    /// </summary>
    /// <param name="gameRunning">Refuses while the client runs. Renaming an MPQ a live client has
    /// mapped either fails on Windows or, worse, succeeds on Linux and leaves the game reading a file
    /// nobody can find.</param>
    public VanillaLocaleResult Apply(string clientDir, string locale, bool gameRunning = false)
    {
        var wanted = locale?.Trim() ?? "";
        if (!IsLocaleCode(wanted))
            return VanillaLocaleResult.Failed(BaseLocale, $"{locale} is not a language code.");

        if (gameRunning)
            return VanillaLocaleResult.Failed(Active(clientDir),
                "The game is running. Close it before switching language.");

        try
        {
            var slot = SlotPath(clientDir);
            var active = Active(clientDir);
            var markerPresent = File.Exists(MarkerPath(clientDir));

            // A pack in the slot that we did not put there is the player's. Refuse before anything is
            // moved: the alternative is silently disabling a mod they installed themselves.
            if (File.Exists(slot) && !markerPresent)
                return VanillaLocaleResult.Failed(BaseLocale,
                    $"There is already a {SlotFileName} in {DataDir(clientDir)} that the launcher did " +
                    "not install. Move it out of the way to use the language switch.");

            // Checked BEFORE anything is moved, so a missing pack never leaves the install half-switched.
            if (wanted != BaseLocale && wanted != active && !File.Exists(PackPath(clientDir, wanted)))
                return VanillaLocaleResult.Failed(active,
                    $"The {wanted} language pack is not installed in this client.");

            if (wanted == active && (wanted == BaseLocale) == !File.Exists(slot))
            {
                // Already in the wanted state. The config keys are still written: a player who edited
                // Config.wtf by hand, or an update that replaced it, must not silently keep the old
                // language while the pack says otherwise.
                WriteConfig(clientDir, wanted);
                DropWdbCache(clientDir);
                return VanillaLocaleResult.Success(wanted);
            }

            // 1. Vacate the slot, putting the current pack back where it belongs.
            if (!Deactivate(clientDir, active, out var deactivateError))
                return VanillaLocaleResult.Failed(active, deactivateError);

            // 2. Bring the wanted pack in (English needs none — it is in the base MPQs).
            if (wanted != BaseLocale)
            {
                var pack = PackPath(clientDir, wanted);
                if (!File.Exists(pack))
                {
                    Reactivate(clientDir, active);
                    return VanillaLocaleResult.Failed(active,
                        $"The {wanted} language pack is not installed in this client.");
                }

                File.Move(pack, slot, overwrite: false);
                // Measured, not assumed: a move that did not happen must not read as one.
                if (!File.Exists(slot) || File.Exists(pack))
                {
                    Reactivate(clientDir, active);
                    return VanillaLocaleResult.Failed(active, $"{wanted} could not be activated.");
                }
                File.WriteAllText(MarkerPath(clientDir), wanted + "\n");
            }

            WriteConfig(clientDir, wanted);
            DropWdbCache(clientDir);
            _log.Information("Client language switched to {Locale} ({Dir})", wanted, clientDir);
            return VanillaLocaleResult.Success(wanted);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Switching the client language to {Locale} failed", wanted);
            return VanillaLocaleResult.Failed(Active(clientDir), "The language could not be switched.");
        }
    }

    /// <summary>Empty the patch slot and put the pack back in its locale folder. Succeeds when there is
    /// nothing to do; fails only when a pack is there and cannot be moved, because carrying on then
    /// would activate a second language on top of the first.</summary>
    private bool Deactivate(string clientDir, string active, out string error)
    {
        error = "";
        var slot = SlotPath(clientDir);
        var marker = MarkerPath(clientDir);

        if (!File.Exists(slot))
        {
            // A marker whose pack is gone is stale bookkeeping, not a failure — the install is already
            // English, which is what the marker's absence would have said.
            if (File.Exists(marker)) TryDelete(marker);
            return true;
        }

        if (active == BaseLocale)
        {
            // Marker says English but a pack is in the slot: we cannot know which language it is, so it
            // is not moved anywhere it might overwrite a real pack.
            error = $"{SlotFileName} is in place but the launcher cannot tell which language it is. " +
                    "Delete it to switch language again.";
            return false;
        }

        var home = PackPath(clientDir, active);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(home)!);
            if (File.Exists(home))
            {
                // Two files claiming to be the same pack. Keeping both is the only answer that cannot
                // destroy one of them.
                error = $"Cannot put the {active} pack back: {home} already exists.";
                return false;
            }
            File.Move(slot, home, overwrite: false);
            if (File.Exists(slot) || !File.Exists(home))
            {
                error = $"The {active} language pack could not be set aside.";
                return false;
            }
            TryDelete(marker);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not deactivate the {Locale} language pack", active);
            error = $"The {active} language pack could not be set aside.";
            return false;
        }
    }

    /// <summary>Put a pack that was just deactivated back into the slot. Best effort by design: this
    /// runs when something already went wrong, and a throw here would replace one problem with two.</summary>
    private void Reactivate(string clientDir, string locale)
    {
        if (locale == BaseLocale) return;
        try
        {
            var slot = SlotPath(clientDir);
            var home = PackPath(clientDir, locale);
            if (File.Exists(slot) || !File.Exists(home)) return;
            File.Move(home, slot, overwrite: false);
            File.WriteAllText(MarkerPath(clientDir), locale + "\n");
            _log.Information("Rolled the {Locale} language pack back into place", locale);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not roll the {Locale} language pack back", locale);
        }
    }

    /// <summary>
    /// The three keys the 1.12.1 client reads. All three, always: the client takes its interface text
    /// from <c>textLocale</c>, its voice-over from <c>audioLocale</c> and its data path from
    /// <c>locale</c>, and setting only one of them produces a client that is half translated and reads
    /// like a broken pack rather than a wrong setting.
    /// </summary>
    private void WriteConfig(string clientDir, string locale)
    {
        try
        {
            var config = Path.Combine(clientDir, "WTF", "Config.wtf");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            foreach (var key in new[] { "locale", "textLocale", "audioLocale" })
                WtfFile.SetVar(config, key, locale);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not write the language keys into Config.wtf ({Dir})", clientDir);
        }
    }

    /// <summary>
    /// Throw away the client's cache of server-supplied text. Quest titles, item and NPC names arrive
    /// from the realm and are cached in <c>WDB/</c> per row, with no language stamp — so after a switch
    /// the client keeps showing the OLD language for everything it has already seen, which reads as a
    /// language pack that only half worked. The cache refills on its own.
    /// </summary>
    private void DropWdbCache(string clientDir)
    {
        try
        {
            var wdb = Path.Combine(clientDir, "WDB");
            if (!Directory.Exists(wdb)) return;
            foreach (var file in Directory.EnumerateFiles(wdb, "*.wdb", SearchOption.AllDirectories))
                TryDelete(file);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not clear the WDB cache in {Dir}", clientDir);
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { _log.Debug(ex, "Could not delete {Path}", path); }
    }
}
