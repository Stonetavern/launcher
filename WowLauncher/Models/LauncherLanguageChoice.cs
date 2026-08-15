namespace WowLauncher.Models;

/// <summary>
/// Ein Eintrag im Sprachwähler der Oberfläche. Der erste ist „der Spielsprache folgen" und trägt
/// einen leeren Code — das ist der Zustand, in dem der Launcher seit jeher lief und der für die
/// meisten Spieler richtig bleibt.
///
/// <para>Die Namen stehen bewusst in ihrer <b>eigenen</b> Sprache: wer den Launcher auf Russisch
/// stellen will, sucht „Русский" und nicht „Russian".</para>
/// </summary>
/// <param name="Code">Der Katalogcode (<c>de</c>, <c>fr</c>, …), oder leer für „folgen".</param>
/// <param name="DisplayName">Wie der Eintrag im Menü steht.</param>
public sealed record LauncherLanguageChoice(string Code, string DisplayName)
{
    public static IReadOnlyList<LauncherLanguageChoice> All { get; } =
    [
        new("", Localization.Loc.T("Settings_Language_FollowGame")),
        new("en", "English"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new("ru", "Русский"),
    ];
}
