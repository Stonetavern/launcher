namespace WowLauncher.Tests;

using System;
using System.Reflection;
using System.Reflection.Emit;
using WowLauncher.Services;
using Xunit;

/// <summary>
/// Regression proofs for the self-update LOOP that hit players on 2026-08-01.
///
/// <para><b>What happened.</b> <c>UpdateService</c> compared the manifest against
/// <c>Assembly.GetName().Version</c> — the AssemblyVersion. The csproj pinned that at 1.1.0.0 and only
/// ever bumped <c>&lt;Version&gt;</c>, so the SHIPPED 1.5.1 AppImage reported itself as 1.1.0.0 (verified
/// by reading the published binary). The manifest advertised 1.5.1. Every start therefore found an
/// update, downloaded it, swapped it in — and the fresh binary reported 1.1.0.0 again. Forever.</para>
///
/// <para><b>Why the old suite missed it.</b> Every existing update test passes <c>currentVersion</c>
/// explicitly, so the default resolution — the only code path a real player ever runs — was never
/// executed by a single test. These tests exercise that path instead of trusting it.</para>
/// </summary>
public class UpdateVersionLoopTests
{
    /// <summary>Builds a throwaway in-memory assembly carrying exactly the version pair that shipped:
    /// AssemblyVersion 1.1.0.0, InformationalVersion "1.5.1+&lt;sha&gt;". Reproducing the real artefact
    /// rather than asserting on hand-picked strings is what makes this a proof and not a restatement.</summary>
    private static Assembly ShippedLikeAssembly(string assemblyVersion, string? informational)
    {
        var name = new AssemblyName("LoopProbe") { Version = Version.Parse(assemblyVersion) };
        var asm = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.RunAndCollect);
        if (informational is not null)
        {
            var ctor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!;
            asm.SetCustomAttribute(new CustomAttributeBuilder(ctor, [informational]));
        }
        return asm;
    }

    /// <summary>THE loop test: the shipped 1.5.1 must recognise itself as 1.5.1, not as 1.1.0.0.
    /// Revert <c>RunningVersion</c> to <c>asm.GetName().Version</c> and this goes red.</summary>
    [Fact]
    public void RunningVersion_ReadsProductVersion_NotTheFrozenAssemblyVersion()
    {
        var shipped = ShippedLikeAssembly("1.1.0.0", "1.5.1+d6c40ded69b05770bd19fc6dd02bcabcf89d5bed");
        Assert.Equal(new Version(1, 5, 1), UpdateService.RunningVersion(shipped));
    }

    /// <summary>The consequence, stated as the player experiences it: a launcher that IS the advertised
    /// version must not consider itself out of date. This is the assertion that would have caught the
    /// live bug at build time.</summary>
    [Fact]
    public void ShippedBuild_DoesNotSeeItsOwnManifestEntryAsAnUpdate()
    {
        var running = UpdateService.RunningVersion(ShippedLikeAssembly("1.1.0.0", "1.5.1+abc123"));
        var advertised = Version.Parse("1.5.1");
        Assert.True(UpdateService.CompareVersions(advertised, running) <= 0,
            "a build must never be offered the version it already is — that is the update loop");
    }

    [Fact]
    public void RunningVersion_FallsBackToAssemblyVersion_WhenInformationalIsMissingOrJunk()
    {
        Assert.Equal(new Version(2, 3, 4, 0), UpdateService.RunningVersion(ShippedLikeAssembly("2.3.4.0", null)));
        Assert.Equal(new Version(2, 3, 4, 0), UpdateService.RunningVersion(ShippedLikeAssembly("2.3.4.0", "nightly")));
    }

    /// <summary>Component count must not decide who is newer. <see cref="Version"/> stores an absent
    /// component as -1, so plain <c>&lt;</c> makes 1.6.0 older than 1.6.0.0 — the same endless update
    /// with different digits, triggered by nothing worse than a manifest written with four components.</summary>
    [Theory]
    [InlineData("1.6.0", "1.6.0.0", 0)]
    [InlineData("1.6", "1.6.0.0", 0)]
    [InlineData("1.6.1", "1.6.0.0", 1)]
    [InlineData("1.5.1", "1.6.0", -1)]
    public void CompareVersions_TreatsMissingComponentsAsZero(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(UpdateService.CompareVersions(Version.Parse(a), Version.Parse(b))));

    /// <summary>Guards the fix at its source: the three version fields in WowLauncher.csproj have to move
    /// together. The loop existed precisely because <c>&lt;Version&gt;</c> was bumped six times while
    /// <c>&lt;AssemblyVersion&gt;</c> stayed behind, and nothing in the build objected.</summary>
    [Fact]
    public void ProductAndAssemblyVersionOfTheRealLauncher_AgreeOnMajorMinorPatch()
    {
        var asm = typeof(UpdateService).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(informational), "the launcher must carry a product version");

        var product = Version.Parse(informational!.Split('+', '-')[0]);
        var assemblyVersion = asm.GetName().Version!;
        Assert.Equal(0, UpdateService.CompareVersions(product, assemblyVersion));
    }
}
