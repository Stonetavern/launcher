namespace WowLauncher.Models;

public static class ClientLocales
{
    /// <summary>
    /// Shipped client locales. v1 ships English-only (enUS): the private-server de-facto
    /// standard, matches the hosted 1.12 Vanilla client exactly, and avoids the deDE
    /// client-build problem (an enUS WoW.exe always reports enUS to realmd regardless of
    /// locale MPQs — see clients/HANDOFF-client-i18n). Other locales return here once a
    /// genuine localized client build is hosted.
    /// </summary>
    public static readonly LocaleInfo[] SupportedLocales =
    [
        new("enUS", "English"),
    ];

    public static LocaleInfo Default => SupportedLocales[0]; // enUS
    public static LocaleInfo FromCode(string code) =>
        SupportedLocales.FirstOrDefault(l => l.Code == code) ?? Default;
}

public sealed record LocaleInfo(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}
