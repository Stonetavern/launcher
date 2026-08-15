using System;
using System.IO;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Which Wine the modern client is started with. The rule has one shape and two halves: wine-ge wins
/// when it is there (most evidence behind it), the system Wine is a full second choice (measured
/// working end to end on 2026-08-03), and only "no Wine anywhere" is a refusal.
///
/// <para>Before this, a machine without Lutris was refused outright while the bundle's own shell script
/// started the same client fine on the system Wine — the launcher was stricter than its own package,
/// and a real player was caught by it (Discord 2026-07-26).</para>
/// </summary>
public sealed class ModernWineRuntimeTests
{
    [Fact]
    public void WineGe_WinsWhenBothArePresent()
    {
        var choice = ModernWineRuntime.Resolve(() => "/runners/wine-ge-8-26/bin/wine", () => "/usr/bin/wine");

        Assert.NotNull(choice);
        Assert.Equal("/runners/wine-ge-8-26/bin/wine", choice!.Path);
        Assert.True(choice.IsWineGe);
    }

    /// <summary>The case that used to be a refusal.</summary>
    [Fact]
    public void SystemWine_IsUsedWhenThereIsNoWineGe()
    {
        var choice = ModernWineRuntime.Resolve(() => null, () => "/usr/bin/wine");

        Assert.NotNull(choice);
        Assert.Equal("/usr/bin/wine", choice!.Path);
        Assert.False(choice.IsWineGe);
    }

    [Fact]
    public void NoWineAnywhere_IsStillNothing()
    {
        Assert.Null(ModernWineRuntime.Resolve(() => null, () => null));
        Assert.Null(ModernWineRuntime.Resolve(() => "", () => ""));
    }

    [Fact]
    public void TheFirstExecutableWineOnPathWins()
    {
        var path = string.Join(Path.PathSeparator, "/nope", "/opt/bin", "/usr/bin");

        var found = ModernWineRuntime.FindSystemWine(
            path, p => p == Path.Combine("/opt/bin", "wine") || p == Path.Combine("/usr/bin", "wine"));

        Assert.Equal(Path.Combine("/opt/bin", "wine"), found);
    }

    /// <summary>Present but not executable is NOT found. Handing an unexecutable file to a process start
    /// would turn "no Wine installed" into an unexplained launch failure further downstream.</summary>
    [Fact]
    public void AWineThatCannotBeExecuted_DoesNotCount()
    {
        var found = ModernWineRuntime.FindSystemWine("/usr/bin", _ => false);

        Assert.Null(found);
    }

    [Fact]
    public void AnEmptyOrMissingPath_IsNotSearched()
    {
        Assert.Null(ModernWineRuntime.FindSystemWine(null, _ => true));
        Assert.Null(ModernWineRuntime.FindSystemWine("", _ => true));
    }

    /// <summary>Off Linux nothing is resolved here: macOS brings its own Wine (GPTK) and Windows has no
    /// business picking one up at all.</summary>
    [Fact]
    public void OffLinux_NothingIsResolved()
    {
        if (OperatingSystem.IsLinux()) return;
        Assert.Null(ModernWineRuntime.ResolveForCurrentUser());
    }
}
