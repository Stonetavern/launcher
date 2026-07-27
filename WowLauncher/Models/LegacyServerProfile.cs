namespace WowLauncher.Models;

/// <summary>
/// The pre-2026-07-21 "server profile" shape, kept only so an existing <c>launcher_config.json</c> can
/// be read and migrated into <see cref="RealmEntry"/> exactly once (see <c>ConfigService</c>).
///
/// <para>It was replaced because the launcher had two words for one thing: a "server" in settings and a
/// "realm" in the v3 rail, with no link between them. A realm now carries its address <em>and</em> the
/// client build it needs, which is what makes assigning a downloaded client to a realm possible at all.</para>
///
/// <para>Nothing writes this type any more. Once every config in the wild has been through one launch,
/// it can go.</para>
/// </summary>
public sealed class LegacyServerProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RealmlistAddress { get; set; } = "";
    public string? ManifestUrl { get; set; }
    public bool IsBuiltIn { get; set; }

    /// <summary>Map onto the current model. Legacy profiles never recorded a client build, so they
    /// inherit the default (1.12.1) - the only build the launcher shipped while they existed.</summary>
    public RealmEntry ToRealm() => new()
    {
        Id = Id,
        Name = Name,
        RealmlistAddress = RealmlistAddress,
        ManifestUrl = ManifestUrl,
        ClientKey = "1.12.1",
        IsPreset = false,
        IsLive = true,
    };
}
