namespace WowLauncher.Models;

public static class ClientLocales
{
    /// <summary>
    /// Every locale this launcher knows how to name, in the order a language menu should show them.
    /// This is a NAMING table, not an availability list — what a given installation can actually be
    /// started in is read from that installation
    /// (<see cref="WowLauncher.Services.Platform.InstalledClientLocales"/>), because a locale the
    /// client does not carry does not degrade to English: it kills the client with ERROR #134 before
    /// a window appears (measured 2026-08-03 against the local 1.14.2 install).
    ///
    /// <para>Names are written in the language itself. A player looking for their own language scans
    /// for "Deutsch", not for "German" — and the ones who need this menu most are the ones who cannot
    /// read the language the launcher is currently in.</para>
    /// </summary>
    public static readonly LocaleInfo[] KnownLocales =
    [
        new("enUS", "English"),
        new("deDE", "Deutsch"),
        new("frFR", "Français"),
        new("esES", "Español (España)"),
        new("esMX", "Español (México)"),
        new("ptBR", "Português (Brasil)"),
        new("ruRU", "Русский"),
        new("koKR", "한국어"),
        new("zhCN", "简体中文"),
        new("zhTW", "繁體中文"),
        new("itIT", "Italiano"),
    ];

    /// <summary>
    /// What may be offered when the launcher does not know which client is installed, or cannot read
    /// the installation: English alone. The 1.12.1 client takes its language from separately installed
    /// MPQ packs, which this detection does not cover, so it stays on this list until those ship
    /// (PLAN-i18n-enterprise-2026-08-02.md).
    /// </summary>
    public static readonly LocaleInfo[] SupportedLocales = [KnownLocales[0]];

    public static LocaleInfo Default => KnownLocales[0]; // enUS

    /// <summary>Name a code, falling back to English for anything unknown. Never null: an unknown code
    /// in a saved config must not empty the menu.</summary>
    public static LocaleInfo FromCode(string code) =>
        KnownLocales.FirstOrDefault(l => l.Code == code) ?? Default;

    /// <summary>
    /// The languages the REALM can answer in. vMaNGOS carries four localisation slots beside English
    /// — <c>loc1=frFR</c>, <c>loc2=deDE</c>, <c>loc6=esES</c>, <c>loc8=ruRU</c> — and those are the
    /// only ones a player's client can ask for and get realm text back. The remaining slots
    /// (koKR, zhCN, zhTW, esMX) exist in the table but no path fills them for this realm, and ptBR has
    /// no native Vanilla slot at all.
    ///
    /// <para><b>Why the client's own list is not enough.</b> The 1.14.2 package ships ten languages,
    /// and every one of them makes the client itself German, Korean or Chinese — menus, spell names,
    /// zone names. But quests, items, NPC names and gossip come from the SERVER, and outside those
    /// four slots they come back English. Offering a language the realm cannot answer in produces a
    /// half-translated game and a support thread, which is worse than not offering it. Owner decision
    /// 2026-08-03: only what vMaNGOS can serve goes in the menu.</para>
    ///
    /// <para>Source: <c>(internal design notes, not published)</c>
    /// (slot map I4, and C4 on why the other slots reach nobody).</para>
    ///
    /// <para>🔴 Not measured here: whether each slot is actually FILLED in the live world database.
    /// Slot capability is a property of the core; how much text sits in a slot is a property of the
    /// data, and an empty slot degrades to English per row rather than failing.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> RealmSupported =
        new HashSet<string>(StringComparer.Ordinal) { "enUS", "deDE", "frFR", "esES", "ruRU" };

    /// <summary>Turn the codes an installation carries into a menu, in <see cref="KnownLocales"/>
    /// order, keeping only what the realm can also answer in (<see cref="RealmSupported"/>) and
    /// skipping codes we have no name for (an unnamed row in a language menu is worse than a missing
    /// one). English is always present, because every installation carries it and every realm speaks
    /// it.</summary>
    public static IReadOnlyList<LocaleInfo> ForInstalled(IEnumerable<string>? installedCodes)
    {
        if (installedCodes is null) return SupportedLocales;
        var codes = new HashSet<string>(installedCodes, StringComparer.Ordinal) { Default.Code };
        var menu = KnownLocales
            .Where(l => codes.Contains(l.Code) && RealmSupported.Contains(l.Code))
            .ToList();
        return menu.Count > 0 ? menu : SupportedLocales;
    }
}

public static class LocaleSelection
{
    /// <summary>
    /// Which entry of <paramref name="menu"/> should be selected, given what is selected now
    /// (<paramref name="currentCode"/>, null when the picker has just cleared it) and what the player
    /// saved (<paramref name="savedCode"/>).
    ///
    /// <para><b>Why this exists.</b> A ComboBox drops a selection that is not in its ItemsSource and
    /// writes the null back through the binding. The language menu is read from the installation and
    /// from the manifest, so it arrives a moment AFTER the window: the first frame offers English
    /// only, and a player whose saved language is German has it cleared before the real menu lands.
    /// The box then shows NOTHING while the client is German - which reads as a broken launcher, and
    /// is visible only in the rendered window (Linux end-to-end run, 2026-08-04).</para>
    ///
    /// <para>Never returns null for a non-empty menu: an empty language box is worse than a wrong
    /// one, because it tells the player nothing at all.</para>
    /// </summary>
    public static LocaleInfo? Repair(IReadOnlyList<LocaleInfo> menu, string? currentCode, string? savedCode)
    {
        if (menu is null || menu.Count == 0) return null;

        return Find(menu, currentCode)
               ?? Find(menu, savedCode)
               ?? Find(menu, ClientLocales.Default.Code)
               ?? menu[0];
    }

    private static LocaleInfo? Find(IReadOnlyList<LocaleInfo> menu, string? code) =>
        string.IsNullOrEmpty(code) ? null : menu.FirstOrDefault(l => l.Code == code);
}

public sealed record LocaleInfo(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}
