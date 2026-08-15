namespace WowLauncher.Services;

/// <summary>What happened to a profile switch, and why. Never an exception: a profile is a
/// convenience, and losing a launch over one would be the wrong trade.</summary>
public sealed record AddonProfileResult(bool Ok, string Profile, string? Error = null)
{
    public static AddonProfileResult Success(string profile) => new(true, profile);
    public static AddonProfileResult Failed(string profile, string error) => new(false, profile, error);
}

/// <summary>
/// One addon set per realm, the way a Minecraft launcher keeps one mod set per instance.
///
/// <para><b>The problem it solves.</b> Elwynn and Barrens share one client directory, so they shared
/// one <c>Interface/AddOns</c>. A player who wants a raid UI on one realm and a bare screen on the
/// other had to move folders by hand, and the launcher happily reported both realms as carrying
/// whatever the last one installed.</para>
///
/// <para><b>How it works, and why this way.</b> The ACTIVE profile is always the real
/// <c>Interface/AddOns</c> — the game knows no other path, and nothing may depend on the launcher
/// running. Inactive profiles sit beside it as <c>Interface/AddOns.&lt;profile&gt;</c>. Switching is
/// two directory renames, which is instant on any filesystem, needs no elevation (unlike symlinks on
/// Windows) and never copies a byte. A marker file inside the active folder records whose it is, so
/// the launcher can tell "Elwynn's addons" from "whatever was there" after a crash, an update, or a
/// player moving things around.</para>
///
/// <para><b>Rules learned the expensive way in this project.</b> A rename is only believed after the
/// result is read back off the disk (a failed rename that reports success loses a player's addons).
/// A half-finished switch is rolled back rather than left in place. And a folder the launcher did not
/// create is never thrown away: an existing AddOns with no marker is ADOPTED as the current realm's
/// profile, because it is the player's, not ours.</para>
/// </summary>
public sealed class AddonProfileService
{
    /// <summary>Written inside the active AddOns folder. Names the profile it belongs to, so a switch
    /// knows what it is switching away from without keeping state anywhere else.</summary>
    public const string MarkerName = ".stonetavern-profile";

    /// <summary>Der einzige Satz, den es ab 1.7.8 gibt. Frueher fuehrte der Launcher einen Satz pro
    /// Realm (elwynn, barrens); auf Owner-Entscheid 2026-08-12 gibt es nur noch diesen, und er ist
    /// nicht loeschbar.</summary>
    public const string DefaultProfile = "default";

    /// <summary>
    /// Bringt eine Installation aus der Zeit der Realm-Saetze auf den einen Default.
    ///
    /// <para>🔴 Es wird NICHTS geloescht und nichts verschoben. Der aktive Satz bleibt genau der, der
    /// aktiv ist — es wird ausschliesslich die Marker-Datei umgeschrieben. Geparkte Saetze
    /// (<c>Interface/AddOns.barrens</c> und dergleichen) bleiben unberuehrt liegen: das sind die
    /// Addons des Spielers, die hat der Launcher nicht angelegt und wirft er nicht weg. Sie tauchen
    /// nur nicht mehr in der Oberflaeche auf. Wer sie zurueckwill, benennt den Ordner von Hand um —
    /// besser, als wenn eine Migration sie stillschweigend entfernt.</para>
    ///
    /// <para>Gibt zurueck, welcher Satz vorher aktiv war, oder null wenn schon alles auf Default
    /// stand. Der Rueckgabewert ist fuers Log da, damit im Zweifel nachlesbar ist, was der Spieler
    /// vorher hatte.</para>
    /// </summary>
    public string? MigrateToDefault(string clientDir)
    {
        var active = ActiveProfile(clientDir);
        if (string.Equals(active, DefaultProfile, StringComparison.OrdinalIgnoreCase)) return null;

        var addons = AddonsDir(clientDir);
        if (!Directory.Exists(addons)) return null;

        try
        {
            File.WriteAllText(Path.Combine(addons, MarkerName), DefaultProfile);
        }
        catch (Exception ex)
        {
            // Kein Grund zum Abbrechen: ohne Marker wird der vorhandene Ordner beim naechsten Blick
            // ohnehin als aktueller Satz adoptiert (siehe Klassen-Doku). Nur sagen muss man es.
            _log.Warning(ex, "Could not write the default addon profile marker in {Dir}", addons);
            return active;
        }

        var parked = KnownProfiles(clientDir)
            .Where(n => !string.Equals(n, DefaultProfile, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _log.Information(
            "Addon profiles reduced to the default set (was {Was}). Parked sets left untouched on disk: {Parked}",
            active ?? "(unmarked)", parked.Count == 0 ? "none" : string.Join(", ", parked));

        return active;
    }


    private readonly Serilog.ILogger _log;

    public AddonProfileService(Serilog.ILogger log) => _log = log.ForContext<AddonProfileService>();

    /// <summary>The addons folder of a client install (the same resolution the addon installer uses),
    /// with its parent, so a profile can be parked beside it.</summary>
    private static string AddonsDir(string clientDir) => AddonService.AddonsDir(clientDir);

    private static string ProfileDir(string clientDir, string profile) =>
        AddonsDir(clientDir) + "." + Sanitise(profile);

    /// <summary>Profile names come from realm ids, which are ours — but a hand-edited config can put
    /// anything in one, and that anything would become a path. Only letters, digits, dash and
    /// underscore survive; everything else is dropped rather than escaped.</summary>
    internal static string Sanitise(string profile)
    {
        var clean = new string((profile ?? "").Where(c =>
            char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return clean.Length == 0 ? "default" : clean.ToLowerInvariant();
    }

    /// <summary>Which profile the current <c>Interface/AddOns</c> belongs to, or null when it carries
    /// no marker (a folder the player or an older launcher made).</summary>
    public string? ActiveProfile(string clientDir)
    {
        try
        {
            var marker = Path.Combine(AddonsDir(clientDir), MarkerName);
            if (!File.Exists(marker)) return null;
            var name = File.ReadAllText(marker).Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not read the addon profile marker in {Dir}", clientDir);
            return null;
        }
    }

    /// <summary>Every profile that exists for this install, active one included, in name order.</summary>
    public IReadOnlyList<string> KnownProfiles(string clientDir)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var active = ActiveProfile(clientDir);
            if (active is not null) names.Add(active);

            var addons = AddonsDir(clientDir);
            var parent = Path.GetDirectoryName(addons);
            var prefix = Path.GetFileName(addons) + ".";
            if (parent is not null && Directory.Exists(parent))
            {
                // Filtered here rather than by a search pattern: a pattern containing a dot goes
                // through the runtime's DOS-8.3 compatibility rules and quietly matched nothing, so
                // every parked profile was invisible while sitting right there on disk.
                foreach (var dir in Directory.EnumerateDirectories(parent))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length)
                        names.Add(name[prefix.Length..]);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not list addon profiles in {Dir}", clientDir);
        }
        return names.ToList();
    }

    /// <summary>
    /// Make <paramref name="profile"/> the active addon set for this install. Idempotent: switching to
    /// the profile that is already active does nothing and reports success.
    /// </summary>
    /// <remarks>
    /// Refuses while the game is running (<paramref name="gameRunning"/>): renaming the folder a live
    /// client has open either fails outright on Windows or, worse, succeeds on Linux and leaves the
    /// running game reading a directory nobody can see.
    /// </remarks>
    public AddonProfileResult Switch(string clientDir, string profile, bool gameRunning = false)
    {
        var wanted = Sanitise(profile);
        if (gameRunning)
            return AddonProfileResult.Failed(wanted,
                "The game is running. Close it before switching addon profiles.");

        try
        {
            var addons = AddonsDir(clientDir);
            var active = ActiveProfile(clientDir);

            if (Directory.Exists(addons) && active is null)
            {
                // An AddOns folder with no marker predates profiles or was made by hand. It is the
                // player's, so it becomes the profile they are on right now instead of being parked
                // under a name they never chose or, worse, replaced.
                _log.Information(
                    "Adopting the existing AddOns folder as profile {Profile} (it carried no marker)", wanted);
                return WriteMarker(addons, wanted)
                    ? AddonProfileResult.Success(wanted)
                    : AddonProfileResult.Failed(wanted,
                        "The addon set could not be marked on disk. Nothing was changed.");
            }

            if (string.Equals(active, wanted, StringComparison.OrdinalIgnoreCase))
                return AddonProfileResult.Success(wanted);

            var target = ProfileDir(clientDir, wanted);

            // 1. Park the active set under its own name, if there is one.
            string? parked = null;
            if (Directory.Exists(addons) && active is not null)
            {
                parked = ProfileDir(clientDir, active);
                if (Directory.Exists(parked))
                    return AddonProfileResult.Failed(wanted,
                        $"Cannot park the current addons: {parked} already exists. Move it out of the way.");
                Directory.Move(addons, parked);
                // Measured, not assumed: a move that did not happen must not read as one.
                if (Directory.Exists(addons) || !Directory.Exists(parked))
                    return AddonProfileResult.Failed(wanted, "The current addons could not be set aside.");
            }

            // 2. Bring the wanted set in, or start an empty one.
            if (Directory.Exists(target))
            {
                Directory.Move(target, addons);
                if (!Directory.Exists(addons))
                {
                    RollBack(parked, addons);
                    return AddonProfileResult.Failed(wanted, $"Profile {wanted} could not be activated.");
                }
            }
            else
            {
                Directory.CreateDirectory(addons);
            }

            if (!WriteMarker(addons, wanted))
            {
                // Die Dateien liegen richtig, das Etikett haelt nicht. Beides zusammen ist der
                // Zustand, den ein Spieler bekommen soll - eines ohne das andere waere ein Ordner,
                // dessen Aufschrift beim naechsten Start geraten wird. Also zurueck auf den Stand
                // von vorher und ehrlich melden.
                if (Directory.Exists(target) || parked is not null)
                {
                    TryMoveBack(addons, target);
                    RollBack(parked, addons);
                }
                return AddonProfileResult.Failed(wanted,
                    "The addon set could not be marked on disk. Nothing was changed.");
            }
            _log.Information("Addon profile switched to {Profile} ({Dir})", wanted, addons);
            return AddonProfileResult.Success(wanted);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Switching to addon profile {Profile} failed", wanted);
            return AddonProfileResult.Failed(wanted, "The addon profile could not be switched.");
        }
    }

    /// <summary>Den gerade aktivierten Satz zurueck an seinen Parkplatz schieben. Best effort wie
    /// <see cref="RollBack"/>: es laeuft, wenn ohnehin schon etwas schiefging.</summary>
    private void TryMoveBack(string addons, string target)
    {
        if (!Directory.Exists(addons) || Directory.Exists(target)) return;
        try
        {
            Directory.Move(addons, target);
            _log.Information("Moved the half-activated set back to {Target}", target);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not move the half-activated set back to {Target}", target);
        }
    }

    /// <summary>Put the parked set back where it was. Best effort by design: this runs when something
    /// already went wrong, and a throw here would replace one problem with two.</summary>
    private void RollBack(string? parked, string addons)
    {
        if (parked is null || !Directory.Exists(parked) || Directory.Exists(addons)) return;
        try
        {
            Directory.Move(parked, addons);
            _log.Information("Rolled the previous addon set back into place");
        }
        catch (Exception ex)
        {
            _log.Error(ex,
                "Could not roll the previous addon set back. It is still on disk at {Parked}", parked);
        }
    }

    /// <summary>
    /// Schreibt die Merkdatei — und liest sie zurück. Gibt zurück, ob die Zuordnung auf der Platte
    /// wirklich steht.
    ///
    /// <para>🔴 Vorher schluckte diese Methode Schreibfehler, und <c>Switch</c> meldete trotzdem
    /// Erfolg (Befund einer Zweitinstanz, 2026-08-05). Der Kommentar dazu sagte, ohne Marker sei die
    /// Richtung sicher — der Ordner werde beim nächsten Blick einfach adoptiert. Das stimmt für die
    /// DATEIEN und nicht für das ETIKETT: adoptiert wird für den <b>ersten</b> Namen in der Liste.
    /// Ein Spieler bekäme also „Barrens ist aktiv", die Barrens-Addons lägen richtig, und beim
    /// nächsten Start stünde „Elwynn" über demselben Ordner. Plausibel und falsch, ohne dass etwas
    /// wirft.</para>
    ///
    /// <para>Dieselbe Regel wie beim Umbenennen: erst glauben, wenn es von der Platte
    /// zurückgelesen ist. Bis heute galt sie nur für die eine Hälfte des Mechanismus.</para>
    /// </summary>
    private bool WriteMarker(string addonsDir, string profile)
    {
        try
        {
            Directory.CreateDirectory(addonsDir);
            var path = Path.Combine(addonsDir, MarkerName);
            File.WriteAllText(path, profile + "\n");

            // Gemessen, nicht angenommen: ein Schreibvorgang, der nichts wirft, hat noch nichts
            // bewiesen (volle Platte, schreibgeschützter Ordner, Dateisystem ohne Rechte).
            var readBack = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            if (!string.Equals(readBack, profile, StringComparison.OrdinalIgnoreCase))
            {
                _log.Error("Marker in {Dir} reads back as {Read}, expected {Profile}",
                    addonsDir, readBack.Length == 0 ? "<nothing>" : readBack, profile);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not mark {Dir} as profile {Profile}", addonsDir, profile);
            return false;
        }
    }
}
