using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// 2026-09-23, player report "Linux Launcher Update broke my game": the modern client on Linux fell back
/// to system Wine because GE-Proton was only looked for in Steam's folders. These prove the launcher now
/// finds and, when missing, fetches the same pinned GE-Proton the 1.12 START.sh uses.
/// </summary>
public sealed class PinnedGeProtonTests : IDisposable
{
    private static readonly Serilog.ILogger Log = Serilog.Log.Logger;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "st-geproton-" + Guid.NewGuid().ToString("N"));

    public PinnedGeProtonTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Folders_match_START_sh_defaults_and_overrides()
    {
        Func<string, string?> none = _ => null;
        Assert.Equal("/h/.local/share/stonetavern/runtime/ge-proton", PinnedGeProton.RunnersDir(none, "/h"));
        Assert.Equal("/h/.cache/stonetavern", PinnedGeProton.CacheDir(none, "/h"));
        Assert.Equal("/x/stonetavern/runtime/ge-proton",
            PinnedGeProton.RunnersDir(k => k == "XDG_DATA_HOME" ? "/x" : null, "/h"));
        Assert.Equal("/d/runtime/ge-proton",
            PinnedGeProton.RunnersDir(k => k == "STONETAVERN_DATA_DIR" ? "/d" : null, "/h"));
        Assert.Equal("GE-Proton11-7-x86_64.tar.gz", PinnedGeProton.ArchiveName);
    }

    [Fact]
    public void Locator_searches_the_Stonetavern_folder_not_only_Steam()
    {
        var dirs = GeProtonLocator.AllRunnersDirsFor(_ => null, "/h");
        Assert.Contains("/h/.local/share/stonetavern/runtime/ge-proton", dirs);
        Assert.Contains("/h/.local/share/Steam/compatibilitytools.d", dirs);
    }

    [Fact]
    public void Locator_finds_a_pinned_install_without_Steam()
    {
        if (!OperatingSystem.IsLinux()) return;
        var home = Path.Combine(_root, "home");
        var runners = PinnedGeProton.RunnersDir(_ => null, home);
        MakeFakeProton(Path.Combine(runners, PinnedGeProton.DirName));

        var found = GeProtonLocator.FindLatest(GeProtonLocator.AllRunnersDirsFor(_ => null, home));

        Assert.Equal(Path.Combine(runners, PinnedGeProton.DirName, "proton"), found);
    }

    [Fact]
    public void Modern_client_needs_the_runtime_only_when_no_GE_Proton_exists()
    {
        var installer = new PinnedGeProtonInstaller(new ArchiveDownloads(""), Log, "/r", "/c", () => null);
        var without = new LinuxStartScriptProvisioner(Log, _ => false, null, () => null, installer, () => true);
        var with = new LinuxStartScriptProvisioner(Log, _ => false, null, () => "/s/GE-Proton11-7/proton", installer, () => true);
        var oldPython = new LinuxStartScriptProvisioner(Log, _ => false, null, () => null, installer, () => false);
        // Ubuntu 22.04 (Python 3.10): GE-Proton cannot run there, so nothing is downloaded.
        Assert.False(oldPython.NeedsRuntimeForExe("/g/World of Warcraft/_classic_era_/WowClassic.exe"));

        Assert.True(without.NeedsRuntimeForExe("/g/World of Warcraft/_classic_era_/WowClassic.exe"));
        Assert.False(with.NeedsRuntimeForExe("/g/World of Warcraft/_classic_era_/WowClassic.exe"));
        // 1.12 without START.sh is unchanged: nothing to provision.
        Assert.False(without.NeedsRuntimeForExe("/g/WoW.exe"));
    }

    [Fact]
    public async Task Installer_downloads_verifies_unpacks_and_marks_like_START_sh()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (archive, sha) = BuildFakeArchive(complete: true);
        var runners = Path.Combine(_root, "data", "runtime", "ge-proton");
        var cache = Path.Combine(_root, "cache");
        var downloads = new ArchiveDownloads(archive);
        var installer = new PinnedGeProtonInstaller(downloads, Log, runners, cache,
            () => GeProtonLocator.FindLatest([runners]), sha256: sha);

        var result = await installer.EnsureAsync(null, null, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, downloads.Downloads);
        Assert.True(File.Exists(Path.Combine(runners, PinnedGeProton.DirName, "proton")));
        Assert.True(File.Exists(Path.Combine(runners, PinnedGeProton.OkMarker)));
        Assert.False(Directory.Exists(runners + ".partial"));

        // Second call: present, no second download.
        Assert.True((await installer.EnsureAsync(null, null, CancellationToken.None)).Ok);
        Assert.Equal(1, downloads.Downloads);
    }

    [Fact]
    public async Task Installer_refuses_an_archive_with_the_wrong_checksum_and_deletes_it()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (archive, _) = BuildFakeArchive(complete: true);
        var runners = Path.Combine(_root, "data", "runtime", "ge-proton");
        var cache = Path.Combine(_root, "cache");
        var installer = new PinnedGeProtonInstaller(new ArchiveDownloads(archive), Log, runners, cache,
            () => GeProtonLocator.FindLatest([runners]), sha256: new string('0', 64));

        var result = await installer.EnsureAsync(null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(runners));
        Assert.False(File.Exists(Path.Combine(cache, PinnedGeProton.ArchiveName)));
    }

    [Fact]
    public async Task Installer_refuses_an_archive_without_wine_and_leaves_nothing_behind()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (archive, sha) = BuildFakeArchive(complete: false);
        var runners = Path.Combine(_root, "data", "runtime", "ge-proton");
        var installer = new PinnedGeProtonInstaller(new ArchiveDownloads(archive), Log, runners,
            Path.Combine(_root, "cache"), () => GeProtonLocator.FindLatest([runners]), sha256: sha);

        var result = await installer.EnsureAsync(null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(runners));
        Assert.False(Directory.Exists(runners + ".partial"));
    }

    private static void MakeFakeProton(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "files", "bin"));
        var proton = Path.Combine(dir, "proton");
        File.WriteAllText(proton, "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(dir, "files", "bin", "wine"), "#!/bin/sh\n");
        File.SetUnixFileMode(proton, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private (string Archive, string Sha) BuildFakeArchive(bool complete)
    {
        var src = Path.Combine(_root, "src-" + complete);
        var dir = Path.Combine(src, PinnedGeProton.DirName);
        MakeFakeProton(dir);
        if (!complete) File.Delete(Path.Combine(dir, "files", "bin", "wine"));
        var archive = Path.Combine(_root, "fake-" + complete + ".tar.gz");
        var psi = new ProcessStartInfo("tar") { UseShellExecute = false };
        foreach (var a in new[] { "-czf", archive, "-C", src, PinnedGeProton.DirName }) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        using var fs = File.OpenRead(archive);
        return (archive, Convert.ToHexStringLower(SHA256.HashData(fs)));
    }

    /// <summary>"Downloads" by copying a prepared archive; the checksum check is real.</summary>
    private sealed class ArchiveDownloads(string source) : IDownloadService
    {
        public int Downloads;

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Downloads++;
            File.Copy(source, destPath, overwrite: true);
            return Task.FromResult(DownloadResult.Success);
        }

        public Task<bool> VerifyHashAsync(string path, string expected, CancellationToken ct = default)
        {
            if (!File.Exists(path)) return Task.FromResult(false);
            using var fs = File.OpenRead(path);
            return Task.FromResult(string.Equals(Convert.ToHexStringLower(SHA256.HashData(fs)), expected,
                StringComparison.OrdinalIgnoreCase));
        }

        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null,
            CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> ExtractClientAsync(string z, string d, IProgress<string>? p = null,
            CancellationToken ct = default) => Task.FromResult(false);
        public Task<ExtractOutcome> ExtractClientWithReasonAsync(string zipPath, string destDir, bool freshInstall,
            IProgress<string>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(ExtractOutcome.Fail(ExtractFailure.Unknown));
    }
}

/// <summary>2026-09-24: the proxy start repairs an install a 1.9.1 per-file update left without the
/// execute bit, instead of failing with "Permission denied".</summary>
public sealed class UnixExecBitTests
{
    [Fact]
    public void Ensure_adds_execute_where_read_is_set_and_only_once()
    {
        if (OperatingSystem.IsWindows()) return;
        var f = Path.GetTempFileName();
        try
        {
            File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Assert.True(UnixExecBit.Ensure(f));
            var mode = File.GetUnixFileMode(f);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
            Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
            Assert.False(UnixExecBit.Ensure(f));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void LooksExecutable_knows_ELF_and_shebang_but_not_data()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(f, [0x7F, (byte)'E', (byte)'L', (byte)'F', 2]);
            Assert.True(UnixExecBit.LooksExecutable(f));
            File.WriteAllText(f, "#!/usr/bin/env bash\n");
            Assert.True(UnixExecBit.LooksExecutable(f));
            File.WriteAllText(f, "MPQ data");
            Assert.False(UnixExecBit.LooksExecutable(f));
        }
        finally { File.Delete(f); }
    }
}

/// <summary>Matrix 2026-09-24: on a Linux without libicu the proxy aborted before opening its port.</summary>
public sealed class IcuProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "st-icu-" + Guid.NewGuid().ToString("N"));
    public IcuProbeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Missing_icu_asks_for_invariant_globalization()
    {
        var env = IcuProbe.ProxyEnvironmentFor([_dir], _ => null);
        Assert.NotNull(env);
        Assert.Equal("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", env!.Value.Key);
        Assert.Equal("1", env.Value.Value);
    }

    [Fact]
    public void Installed_icu_needs_nothing()
    {
        File.WriteAllText(Path.Combine(_dir, "libicuuc.so.74"), "");
        Assert.Null(IcuProbe.ProxyEnvironmentFor([_dir], _ => null));
    }

    [Fact]
    public void Icu_on_LD_LIBRARY_PATH_counts_and_an_explicit_setting_wins()
    {
        File.WriteAllText(Path.Combine(_dir, "libicuuc.so.76.1"), "");
        Assert.Null(IcuProbe.ProxyEnvironmentFor(["/nonexistent"], k => k == "LD_LIBRARY_PATH" ? _dir : null));
        Assert.Null(IcuProbe.ProxyEnvironmentFor(["/nonexistent"], k => k == IcuProbe.InvariantVariable ? "0" : null));
    }
}

public sealed class ProtonPythonTests
{
    [Theory]
    [InlineData("3.10", false)]
    [InlineData("3.11", true)]
    [InlineData("3.12.4", true)]
    [InlineData("4.0", true)]
    [InlineData("2.7", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("garbage", false)]
    public void Needs_at_least_3_11(string? version, bool expected) =>
        Assert.Equal(expected, ProtonPython.IsEnough(version));
}
