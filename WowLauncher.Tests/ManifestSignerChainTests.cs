using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Drives the REAL signer — <c>(internal design notes, not published)</c> — and verifies its
/// output with the launcher's own code. Codex review 2026-07-27 named this precisely: python's
/// <c>cryptography</c> emits DER, the launcher expects the fixed-width r||s form, and the conversion
/// between them is asserted in a comment on one side and a constant on the other. A C#-signs /
/// C#-verifies test stays green through exactly that bug, so it proves nothing about the chain that
/// will actually be used on release day.
///
/// <para>The checked-in fixture in <see cref="ManifestSignatureTests"/> pins the encoding for every
/// run; THIS class additionally proves the live script — including its refusal to sign a manifest that
/// the launcher would then reject anyway. It skips (never silently passes) where python3 or
/// <c>cryptography</c> is unavailable.</para>
/// </summary>
public sealed class ManifestSignerChainTests : IDisposable
{
    private static Serilog.ILogger Log => new Serilog.LoggerConfiguration().CreateLogger();

    private readonly string _work = Path.Combine(Path.GetTempPath(), "st-signer-" + Guid.NewGuid().ToString("N"));

    public ManifestSignerChainTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch { /* best-effort */ }
    }

    [SkippableFact]
    public void PythonSigns_TheLauncherVerifies_AndThePolicyAdmits()
    {
        var script = RequireScript();

        var keyPath = Path.Combine(_work, "release-key.pem");
        Run(script, $"--genkey {keyPath}");
        var publicKey = Run(script, $"--show-pubkey {keyPath}").Trim();
        Assert.NotEmpty(publicKey);

        var manifestPath = Path.Combine(_work, "manifest.json");
        File.WriteAllText(manifestPath, ManifestJson(serial: 5));

        Run(script, $"{manifestPath} --key {keyPath}");

        var manifestBytes = File.ReadAllBytes(manifestPath);
        var signature = File.ReadAllText(manifestPath + ".sig");

        // 1) The encoding survives the language boundary.
        var verdict = new ManifestSignature(publicKey).Verify(manifestBytes, signature);
        Assert.True(verdict.Ok, verdict.Reason);

        // 2) …and the document the script produced also satisfies the release policy, i.e. the signer
        //    and the launcher agree on the mandatory fields, not only on the bytes.
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ServerManifest>(manifestBytes,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.True(new ManifestReleasePolicy(new ZeroFloorStore(), Log).Admit(manifest).Ok);

        // 3) One byte changed after signing and the same chain refuses it.
        File.WriteAllBytes(manifestPath, Append(manifestBytes, (byte)' '));
        Assert.False(new ManifestSignature(publicKey)
            .Verify(File.ReadAllBytes(manifestPath), signature).Ok);
    }

    [SkippableFact]
    public void TheSignerRefusesAManifestTheLauncherWouldReject()
    {
        var script = RequireScript();
        var keyPath = Path.Combine(_work, "key.pem");
        Run(script, $"--genkey {keyPath}");

        foreach (var missing in new[] { "serial", "expires", "channel" })
        {
            var path = Path.Combine(_work, $"without-{missing}.json");
            File.WriteAllText(path, ManifestJson(serial: 1, omit: missing));

            var (exit, output) = TryRun(script, $"{path} --key {keyPath}");

            // Signing an incomplete manifest would produce a perfectly valid signature over a document
            // every client refuses — a failure that only surfaces days later on a player's machine.
            Assert.NotEqual(0, exit);
            Assert.Contains(missing, output, StringComparison.Ordinal);
            Assert.False(File.Exists(path + ".sig"));
        }
    }

    [SkippableFact]
    public void TheSignerRefusesASerialThatDoesNotMoveForward()
    {
        var script = RequireScript();
        var keyPath = Path.Combine(_work, "key.pem");
        Run(script, $"--genkey {keyPath}");

        var previous = Path.Combine(_work, "previous.json");
        File.WriteAllText(previous, ManifestJson(serial: 10));

        foreach (var serial in new[] { 9, 10 })
        {
            var next = Path.Combine(_work, $"next-{serial}.json");
            File.WriteAllText(next, ManifestJson(serial));

            var (exit, output) = TryRun(script, $"{next} --key {keyPath} --previous {previous}");

            // A release whose serial does not increase is a rollback for every launcher that already
            // saw the older manifest. Catching it at the signer is catching it before it is published.
            Assert.NotEqual(0, exit);
            Assert.Contains("serial", output, StringComparison.Ordinal);
        }

        var good = Path.Combine(_work, "next-11.json");
        File.WriteAllText(good, ManifestJson(serial: 11));
        Assert.Equal(0, TryRun(script, $"{good} --key {keyPath} --previous {previous}").Exit);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static string ManifestJson(int serial, string? omit = null)
    {
        var sb = new StringBuilder("{\n  \"product\": \"stonetavern-classic\"");
        if (omit != "serial") sb.Append(",\n  \"serial\": ").Append(serial);
        if (omit != "expires") sb.Append(",\n  \"expires\": \"2099-01-01T00:00:00Z\"");
        if (omit != "channel") sb.Append(",\n  \"channel\": \"stable\"");
        sb.Append("\n}\n");
        return sb.ToString();
    }

    private static byte[] Append(byte[] source, byte extra)
    {
        var result = new byte[source.Length + 1];
        source.CopyTo(result, 0);
        result[^1] = extra;
        return result;
    }

    /// <summary>Path of the signer, or a skip when python3/cryptography is not available here.</summary>
    private static string RequireScript()
    {
        var script = FindScript();
        Skip.If(script is null, "deploy/sign-manifest.py not found from the test output directory");
        var (exit, _) = TryRunRaw("python3", "-c \"import cryptography\"");
        Skip.If(exit != 0, "python3 with the cryptography module is not available on this machine");
        return script!;
    }

    /// <summary>Walks up from the test binary until the repository's deploy directory appears — the
    /// test must not hard-code a checkout location.</summary>
    private static string? FindScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", "sign-manifest.py");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string Run(string script, string args)
    {
        var (exit, output) = TryRun(script, args);
        Assert.True(exit == 0, $"sign-manifest.py {args} exited {exit}:\n{output}");
        return output;
    }

    private static (int Exit, string Output) TryRun(string script, string args) =>
        TryRunRaw("python3", $"{script} {args}");

    private static (int Exit, string Output) TryRunRaw(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>A launcher that has never accepted anything yet — the first-run floor.</summary>
    private sealed class ZeroFloorStore : IManifestTrustStore
    {
        public long? ReadHighestSerial() => 0;

        public void Remember(long serial) { }
    }
}
