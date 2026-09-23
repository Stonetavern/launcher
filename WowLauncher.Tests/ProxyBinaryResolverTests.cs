using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The proxy is not one fixed file name. These tests pin the resolution order that keeps every shipped
/// bundle launchable — the macOS v1.4.0 package (<c>Hermes/bin/JimsProxy-arm64</c>), the Linux one
/// (<c>Hermes/linux/JimsProxy</c>), the Windows one (<c>Hermes/JimsProxy.exe</c>) and the older bundles
/// that still carry <c>HermesProxy</c>. Before the resolver existed, the macOS launcher looked only for
/// <c>Hermes/HermesProxy</c> and refused to start on the package it had just installed.
/// </summary>
public sealed class ProxyBinaryResolverTests
{
    private const string MacProxyDir = "/bundle/Hermes";

    [Fact]
    public void Mac_prefers_HermesProxy_because_JimsProxy_crashes_Wine_clients_on_world_entry()
    {
        // 🔴 The whole point of the macOS ordering. The Mac runs the game under GPTK's Wine, and
        // JimsProxy kills the client on world entry there (#132 at 0x141242199 — measured in-world
        // 2026-06-04 and again 2026-08-24 on the owner's Mac). A bundle that carries both must start the
        // old one. If this test is ever "fixed" by flipping it back, Mac players stop reaching the world.
        var present = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(MacProxyDir, "HermesProxy"),
            Path.Combine(MacProxyDir, "bin", "JimsProxy-arm64"),
            Path.Combine(MacProxyDir, "bin", "JimsProxy-x86_64"),
        };

        foreach (var arch in new[] { Architecture.Arm64, Architecture.X64 })
        {
            var resolved = ProxyBinaryResolver.Resolve(
                MacProxyDir, ProxyBinaryResolver.MacCandidates(arch), present.Contains);
            Assert.Equal(Path.Combine(MacProxyDir, "HermesProxy"), resolved.Path);
            // The legacy binary links against a system OpenSSL 3 — the caller must know, or a Mac
            // without Homebrew gets a proxy that cannot start.
            Assert.True(resolved.RequiresOpenSsl);
        }
    }

    [Fact]
    public void Mac_still_picks_the_right_architecture_when_only_JimsProxy_is_present()
    {
        // A bundle without the legacy binary must still respect Rosetta's one-way street: an Intel Mac
        // cannot exec the arm64 build.
        var present = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(MacProxyDir, "bin", "JimsProxy-arm64"),
            Path.Combine(MacProxyDir, "bin", "JimsProxy-x86_64"),
        };

        var arm = ProxyBinaryResolver.Resolve(
            MacProxyDir, ProxyBinaryResolver.MacCandidates(Architecture.Arm64), present.Contains);
        var intel = ProxyBinaryResolver.Resolve(
            MacProxyDir, ProxyBinaryResolver.MacCandidates(Architecture.X64), present.Contains);

        Assert.Equal(Path.Combine(MacProxyDir, "bin", "JimsProxy-arm64"), arm.Path);
        Assert.Equal(Path.Combine(MacProxyDir, "bin", "JimsProxy-x86_64"), intel.Path);
        Assert.False(arm.RequiresOpenSsl);
        Assert.False(intel.RequiresOpenSsl);
    }

    [Fact]
    public void AppleSilicon_may_fall_back_to_the_x64_binary_because_Rosetta_translates_that_way()
    {
        var onlyIntel = Path.Combine(MacProxyDir, "bin", "JimsProxy-x86_64");

        var resolved = ProxyBinaryResolver.Resolve(
            MacProxyDir,
            ProxyBinaryResolver.MacCandidates(Architecture.Arm64),
            path => path == onlyIntel);

        Assert.Equal(onlyIntel, resolved.Path);
    }

    [Fact]
    public void Intel_never_picks_the_arm64_binary_it_cannot_execute()
    {
        // Rosetta translates x86_64 on Apple Silicon, never arm64 on Intel. Offering the arm64 file to an
        // Intel Mac would swap "package incomplete" for an exec failure with no explanation.
        var onlyArm = Path.Combine(MacProxyDir, "bin", "JimsProxy-arm64");

        var resolved = ProxyBinaryResolver.Resolve(
            MacProxyDir, ProxyBinaryResolver.MacCandidates(Architecture.X64), path => path == onlyArm);

        Assert.DoesNotContain("arm64", resolved.Path);
        // With nothing usable present the resolver names the file the package is supposed to carry, and on
        // Intel that is the universal legacy binary — never the arm64 build it could not exec anyway.
        Assert.Equal(Path.Combine(MacProxyDir, "HermesProxy"), resolved.Path);
    }

    [Fact]
    public void Only_the_legacy_binary_is_flagged_as_needing_OpenSSL()
    {
        var legacy = Path.Combine(MacProxyDir, "HermesProxy");
        var resolved = ProxyBinaryResolver.Resolve(
            MacProxyDir, ProxyBinaryResolver.MacCandidates(Architecture.Arm64), path => path == legacy);

        // The old native proxy links against a system OpenSSL 3; the self-contained JimsProxy builds do
        // not. Flagging it here is what keeps the fail-closed check for it while freeing the new ones.
        Assert.True(resolved.RequiresOpenSsl);
    }

    [Fact]
    public void Mac_still_finds_the_legacy_HermesProxy_of_an_older_bundle()
    {
        var legacy = Path.Combine(MacProxyDir, "HermesProxy");

        var resolved = ProxyBinaryResolver.Resolve(
            MacProxyDir, ProxyBinaryResolver.MacCandidates(Architecture.Arm64), path => path == legacy);

        Assert.Equal(legacy, resolved.Path);
    }

    [Fact]
    public void Nothing_present_yields_the_expected_file_not_the_last_one_probed()
    {
        var resolved = ProxyBinaryResolver.Resolve(
            MacProxyDir, ProxyBinaryResolver.MacCandidates(Architecture.Arm64), _ => false);

        Assert.Equal(Path.Combine(MacProxyDir, "HermesProxy"), resolved.Path);
        Assert.Equal("Hermes/HermesProxy", ProxyBinaryResolver.DisplayName("/bundle", resolved.Path));
    }

    private const string LinuxHermesDir = "/bundle/Hermes";

    [Fact]
    public void Linux_prefers_the_shared_tree_JimsProxy_since_2026_09_19_while_Windows_keeps_JimsProxy()
    {
        // 🔴 Owner measurement 2026-09-19 (Ledger run U, GE-Proton11-7, in the world) supersedes the
        // June rule for Linux: v5.2.1-beta.4 no longer crashes on world entry, so the shared-tree
        // binary at Hermes/bin/JimsProxy-linux-x64 (KONZEPT §13) is now the first candidate.
        var shared = Path.Combine(LinuxHermesDir, "bin", "JimsProxy-linux-x64");
        var oldNew = Path.Combine(LinuxHermesDir, "linux", "JimsProxy");
        var oldLegacy = Path.Combine(LinuxHermesDir, "linux", "HermesProxy");
        var present = new HashSet<string>(StringComparer.Ordinal) { shared, oldNew, oldLegacy };

        Assert.Equal(shared, ProxyBinaryResolver.Resolve(
            LinuxHermesDir, ProxyBinaryResolver.LinuxCandidates, present.Contains).Path);

        var winDir = Path.Combine("/bundle", "Hermes");
        var winOld = Path.Combine(winDir, "HermesProxy.exe");
        Assert.Equal(winOld, ProxyBinaryResolver.Resolve(
            winDir, ProxyBinaryResolver.WindowsCandidates, p => p == winOld).Path);
    }

    [Fact]
    public void Linux_falls_back_to_the_old_perOS_JimsProxy_when_the_shared_tree_binary_is_absent()
    {
        var oldNew = Path.Combine(LinuxHermesDir, "linux", "JimsProxy");

        var resolved = ProxyBinaryResolver.Resolve(
            LinuxHermesDir, ProxyBinaryResolver.LinuxCandidates, p => p == oldNew);

        Assert.Equal(oldNew, resolved.Path);
        Assert.False(resolved.RequiresOpenSsl);
    }

    [Fact]
    public void Linux_falls_back_to_the_old_HermesProxy_when_nothing_newer_is_present()
    {
        var oldLegacy = Path.Combine(LinuxHermesDir, "linux", "HermesProxy");

        var resolved = ProxyBinaryResolver.Resolve(
            LinuxHermesDir, ProxyBinaryResolver.LinuxCandidates, p => p == oldLegacy);

        Assert.Equal(oldLegacy, resolved.Path);
        // The legacy binary needs the system OpenSSL 3 on Linux exactly as it does on macOS - the
        // path prefix ("linux/") must not hide the file name the OpenSSL check keys off.
        Assert.True(resolved.RequiresOpenSsl);
    }

    [Fact]
    public void Linux_nothing_present_names_the_shared_tree_binary_the_package_should_carry()
    {
        var resolved = ProxyBinaryResolver.Resolve(
            LinuxHermesDir, ProxyBinaryResolver.LinuxCandidates, _ => false);

        Assert.Equal(Path.Combine(LinuxHermesDir, "bin", "JimsProxy-linux-x64"), resolved.Path);
    }

    [Fact]
    public void LinuxDataDir_is_Hermes_for_the_shared_tree_binary_but_Hermes_linux_for_the_old_layout()
    {
        var shared = Path.Combine(LinuxHermesDir, "bin", "JimsProxy-linux-x64");
        var old = Path.Combine(LinuxHermesDir, "linux", "JimsProxy");

        Assert.Equal(
            Path.GetFullPath(LinuxHermesDir),
            ProxyBinaryResolver.LinuxDataDir(LinuxHermesDir, shared));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(LinuxHermesDir, "linux")),
            ProxyBinaryResolver.LinuxDataDir(LinuxHermesDir, old));
    }

    [Fact]
    public void Mac_layout_resolves_the_shipped_v140_package()
    {
        var root = Path.Combine(Path.GetTempPath(), "mac-layout-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(root, "World of Warcraft", "_classic_era_");
        var exe = Path.Combine(clientDir, "WowClassic.exe");
        var shipped = Path.Combine(root, "Hermes", "bin", "JimsProxy-arm64");

        var layout = MacModernClientLayout.Resolve(exe, Architecture.Arm64, path => path == shipped);

        Assert.NotNull(layout);
        Assert.Equal(shipped, layout!.ProxyExe);
        Assert.False(layout.ProxyRequiresOpenSsl);
        // The config and the CSV game data stay in Hermes/, one level above the binary. The proxy is
        // started from THERE, or it dies looking for Hermes/CSV/... relative to Hermes/bin/.
        Assert.Equal(Path.Combine(root, "Hermes"), layout.ProxyDir);
    }
}
