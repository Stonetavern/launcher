using System;
using System.Linq;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Which download origin a build belongs to, and the two properties that make a beta channel safe
/// to have at all: the player's config cannot move it, and the two channels cannot poison each
/// other's state.
///
/// <para>The test assembly is built WITHOUT <c>-p:StonetavernChannel</c>, so everything here runs on
/// a stable build — which is the case that must not regress. That the packaged stamp actually
/// arrives in the assembly is a build-time fact and was measured separately (2026-08-31: a plain
/// build reports "not stamped", a build with <c>-p:StonetavernChannel=beta</c> reports "beta").</para>
/// </summary>
public sealed class LauncherChannelTests
{
    [Fact]
    public void AnUnstampedBuild_IsTheLiveChannel()
    {
        Assert.False(LauncherChannel.IsBeta);
        Assert.Equal("stable", LauncherChannel.Name);
        Assert.Equal(LauncherChannel.StableBaseUrl, LauncherChannel.BaseUrl);
        Assert.Equal("https://downloads.stonetavern.app/manifest.json", LauncherChannel.ManifestUrl);
    }

    /// <summary>The release policy's channel is the build's, not a config value. It used to be the
    /// constant "stable" with a note that beta would read from the config — a config value is
    /// something a player can type, and this one is half of a security boundary.</summary>
    [Fact]
    public void TheReleasePolicyChannel_ComesFromTheBuild()
    {
        Assert.Equal(LauncherChannel.Name, ManifestReleasePolicy.DefaultChannel);
    }

    /// <summary>The two origins must differ, or "beta" would silently mean production.</summary>
    [Fact]
    public void TheTwoOrigins_AreNotTheSameHost()
    {
        Assert.NotEqual(LauncherChannel.StableBaseUrl, LauncherChannel.BetaBaseUrl);
        Assert.StartsWith("https://", LauncherChannel.BetaBaseUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// A config written by the OTHER channel's build must be moved back onto this build's origin.
    ///
    /// <para>Without this, installing a beta launcher over an existing install would leave it
    /// fetching manifest and client files from live — the rehearsal would rehearse nothing — and a
    /// tester returning to the stable build would keep pulling from beta forever.</para>
    /// </summary>
    [Fact]
    public void AConfigWrittenByTheOtherChannel_IsMovedBackToThisBuildsOrigin()
    {
        var cfg = new LauncherConfig
        {
            SelectedRealmId = RealmRegistry.StonetavernId,
            PatchServerBaseUrl = LauncherChannel.BetaBaseUrl,
            Realms =
            [
                new RealmEntry
                {
                    Id = RealmRegistry.StonetavernId, Name = "Stonetavern",
                    RealmlistAddress = RealmRegistry.StonetavernAddress,
                    ManifestUrl = LauncherChannel.BetaBaseUrl + "/manifest.json",
                },
            ],
        };

        RealmRegistry.ApplyActiveRealm(cfg);

        Assert.Equal(LauncherChannel.ManifestUrl, cfg.ManifestUrl);
        Assert.Equal(LauncherChannel.BaseUrl, cfg.PatchServerBaseUrl);
    }

    /// <summary>The counter-case, and the one that matters more: an address the player typed is
    /// theirs. Only the two addresses the launcher itself ships may be moved.</summary>
    [Fact]
    public void APlayersOwnManifestAddress_IsNeverMoved()
    {
        const string mine = "https://my-own-server.example/manifest.json";
        var cfg = new LauncherConfig
        {
            SelectedRealmId = RealmRegistry.StonetavernId,
            PatchServerBaseUrl = "https://my-own-server.example",
            Realms =
            [
                new RealmEntry
                {
                    Id = RealmRegistry.StonetavernId, Name = "Stonetavern",
                    RealmlistAddress = RealmRegistry.StonetavernAddress, ManifestUrl = mine,
                },
            ],
        };

        RealmRegistry.ApplyActiveRealm(cfg);

        Assert.Equal(mine, cfg.ManifestUrl);
        Assert.Equal("https://my-own-server.example", cfg.PatchServerBaseUrl);
    }
}
