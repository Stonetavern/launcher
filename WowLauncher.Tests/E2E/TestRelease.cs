using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WowLauncher.Models;
using WowLauncher.Services;

namespace WowLauncher.Tests.E2E;

/// <summary>
/// Authors a release the way the real one is authored: a manifest document, signed offline with a
/// P-256 key, published next to its detached <c>.sig</c>.
///
/// <para>🔴 The key is generated per test and is a REAL key — the launcher's signature check runs in
/// full against its public half. PLAN §3 forbids the alternative (a test mode that switches the
/// check off), and for the reason that matters: a suite that disables the gate proves the update
/// path works for an attacker too.</para>
///
/// <para>Signing matches <c>(internal design notes, not published)</c>: SHA-256 over the
/// exact served bytes, fixed-width r||s, base64. And it signs the BYTES that are published, never a
/// re-serialisation of the same object — the production gate verifies bytes, so anything else would
/// be testing a document nobody ships.</para>
/// </summary>
internal sealed class TestRelease : IDisposable
{
    private readonly ECDsa _key;

    public TestRelease()
    {
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PublicKeyBase64 = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Base64 DER SubjectPublicKeyInfo — the shape <c>ManifestSignature</c> embeds.</summary>
    public string PublicKeyBase64 { get; }

    /// <summary>The bytes most recently published, i.e. what the signature covers.</summary>
    public byte[] LastManifestBytes { get; private set; } = [];

    /// <summary>
    /// Serialises <paramref name="manifest"/>, signs those bytes and publishes both files on
    /// <paramref name="origin"/> under the production names.
    /// </summary>
    /// <param name="corruptOneByte">Publish the manifest with a single byte changed AFTER signing —
    /// the shape a CDN compromise has, and the one case a SHA-256 in the same document cannot catch.</param>
    public void Publish(TestOrigin origin, ServerManifest manifest, bool corruptOneByte = false)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { WriteIndented = true }));
        var signature = Sign(bytes);

        if (corruptOneByte)
        {
            var tampered = (byte[])bytes.Clone();
            // Flip a byte in the middle of the document, not in a place JSON would reject: the point
            // is a file that still parses and no longer verifies.
            var at = FindDigit(tampered);
            tampered[at] = tampered[at] == (byte)'9' ? (byte)'8' : (byte)(tampered[at] + 1);
            bytes = tampered;
        }

        LastManifestBytes = bytes;
        origin.Publish(ManifestPath, bytes, "application/json");
        origin.Publish(SignaturePath, Encoding.ASCII.GetBytes(signature), "text/plain");
    }

    /// <summary>Publish the manifest without its signature — "the server forgot the .sig".</summary>
    public void PublishWithoutSignature(TestOrigin origin, ServerManifest manifest)
    {
        Publish(origin, manifest);
        origin.Unpublish(SignaturePath);
    }

    public string Sign(byte[] payload) =>
        Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>
    /// A gate wired exactly as the shipped one, except that its HTTP client reaches the test origin
    /// and its trusted key is this release's. Same class, same policy, same fail-closed behaviour.
    /// </summary>
    public ManifestSignatureGate Gate(TestOrigin origin, Serilog.ILogger log,
        IManifestTrustStore? store = null, string? channel = null) =>
        new(origin.HttpClientForSignedOrigin(),
            new ManifestSignature(PublicKeyBase64),
            new ManifestReleasePolicy(store ?? new MemoryTrustStore(), log, channel),
            log);

    /// <summary>Path of the manifest on the origin, derived from the production URL rather than typed,
    /// so a change to <see cref="LauncherChannel"/> cannot leave the tests serving the wrong name.</summary>
    public static string ManifestPath => new Uri(LauncherChannel.ManifestUrl).AbsolutePath;

    public static string SignaturePath => ManifestPath + ManifestSignatureGate.SignatureSuffix;

    /// <summary>A manifest that passes the release policy, for tests to modify. Serial, channel and
    /// expiry are filled in because a manifest missing any of them is refused before the update logic
    /// is ever reached — and a test that never reaches the logic proves nothing.</summary>
    public static ServerManifest AdmissibleManifest() => new()
    {
        Product = "stonetavern-classic",
        CurrentVersion = "1.0.0",
        Serial = 1000,
        Channel = ManifestReleasePolicy.DefaultChannel,
        Expires = DateTimeOffset.UtcNow.AddYears(5).ToString("O"),
        Patches = [],
    };

    private static int FindDigit(byte[] json)
    {
        for (var i = json.Length / 3; i < json.Length; i++)
            if (json[i] >= (byte)'0' && json[i] <= (byte)'9') return i;
        throw new InvalidOperationException("no digit to flip — the fixture is not what this helper assumes");
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Anti-rollback floor in memory, so no test touches the real trust file on disk.</summary>
internal sealed class MemoryTrustStore : IManifestTrustStore
{
    private long _highest;

    public List<long> Remembered { get; } = [];

    // 0, never null: the policy reads null as "anti-rollback state unreadable" and REFUSES. A store
    // that answered null on a fresh launcher would refuse every manifest, and every test built on it
    // would be measuring that refusal instead of the thing it claims to measure.
    public long? ReadHighestSerial() => _highest;

    public void Remember(long serial)
    {
        Remembered.Add(serial);
        _highest = Math.Max(_highest, serial);
    }

    /// <summary>Pre-load a floor, i.e. "this launcher has already seen release N".</summary>
    public MemoryTrustStore WithFloor(long serial)
    {
        _highest = serial;
        return this;
    }
}
