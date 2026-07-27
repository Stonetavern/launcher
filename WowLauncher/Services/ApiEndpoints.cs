namespace WowLauncher.Services;

/// <summary>
/// One place that decides which host the account-side APIs talk to: login, friends and armory.
///
/// <para>🔴 <b>It is deliberately NOT derived from the realm the player has selected.</b> It used to
/// be: the realmlist host with the leading <c>play.</c> stripped. That works while the rail only holds
/// Stonetavern realms, but a player can add their own realm ("+" in the rail), and from that moment
/// the derivation pointed every account call at a stranger's host - carrying the <em>Stonetavern</em>
/// bearer token in the Authorization header. A third party would have been handed a working session
/// token for an account on our server, and nothing would have looked wrong on screen.</para>
///
/// <para>The account is a Stonetavern account no matter which realm is selected, so its API lives at a
/// fixed address. Realm-specific probing (is this realm up) stays with
/// <see cref="ServerStatusService"/>, which is the only place a foreign host is legitimately
/// contacted - and it sends no credentials.</para>
///
/// <para>Overridable through <see cref="Models.LauncherConfig.AccountApiBaseUrl"/> for test servers.
/// The override is read from the player's own config file, not from anything a realm can supply.</para>
/// </summary>
internal static class ApiEndpoints
{
    /// <summary>Where the Stonetavern account lives when nothing overrides it.</summary>
    public const string DefaultBase = "https://stonetavern.app/api";

    public static string Base(IConfigService config)
    {
        var configured = config.Load().AccountApiBaseUrl;
        return string.IsNullOrWhiteSpace(configured) ? DefaultBase : configured.TrimEnd('/');
    }
}
