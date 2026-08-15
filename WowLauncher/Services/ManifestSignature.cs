namespace WowLauncher.Services;

using System.Security.Cryptography;
using System.Text;
using WowLauncher.Models;

/// <summary>Why a signature check passed or failed. The reason travels with the verdict because a
/// silently-refused update is worse than no update at all: the log line is the only thing that tells
/// an operator whether the server forgot to publish <c>manifest.json.sig</c> or somebody tampered
/// with the file.</summary>
public readonly record struct SignatureVerdict(bool Ok, string Reason)
{
    public static SignatureVerdict Valid { get; } = new(true, "signature valid");

    public static SignatureVerdict Fail(string reason) => new(false, reason);
}

/// <summary>
/// Verifies the detached signature of <c>manifest.json</c> against a public key that ships INSIDE the
/// launcher binary. This is the layer SHA-256 alone never provided: the manifest names the hash the
/// launcher then checks its own update against, so whoever can rewrite the manifest can rewrite the
/// hash too and the check passes on the attacker's bytes. Only a signature made with a key the CDN
/// never holds turns "this file is intact" into "we published this file".
///
/// <para><b>Why ECDSA P-256 and not Ed25519</b> (deliberate deviation from
/// <c>docs/RESEARCH-launcher-autoupdate-2026-07-27.md</c>, which recommends Ed25519). .NET 10 has no
/// Ed25519 in the BCL — verified against the shipped reference assembly
/// (<c>Microsoft.NETCore.App.Ref/10.0.10/ref/net10.0/System.Security.Cryptography.dll</c>): the only
/// occurrences of the name are the composite PQC algorithm ids <c>MLDsa44WithEd25519</c> /
/// <c>MLDsa65WithEd25519</c>, which need platform ML-DSA support and are a hybrid scheme, not a
/// standalone Ed25519. That leaves three ways to get Ed25519, and each costs more than it buys here:
/// <list type="bullet">
/// <item>NSec/libsodium ships a NATIVE library. The launcher publishes single-file with
/// <c>IncludeNativeLibrariesForSelfExtract</c> banned (Defender dropper heuristic) and no loose DLLs
/// in the release — a native dependency violates a hard invariant.</item>
/// <item>BouncyCastle is pure managed and MIT, but adds roughly ten megabytes to a binary that cannot
/// be trimmed (<c>PublishTrimmed</c> is banned too), for a primitive the BCL already offers.</item>
/// <item>Chaos.NaCl is small and pure managed but unmaintained since 2014 — an unpatched crypto
/// dependency in an update path is the wrong trade.</item>
/// </list>
/// ECDSA over P-256 with SHA-256 is in the BCL, is implemented by the OS crypto stack on Windows,
/// Linux and macOS alike, sits at the same ~128-bit security level as Ed25519, adds zero bytes and
/// zero supply chain. Its one real weakness versus Ed25519 — a bad nonce on the SIGNING side leaks
/// the private key — lands on <c>deploy/sign-manifest.py</c>, an offline script using python
/// <c>cryptography</c>'s RNG, not on the launcher. Signature malleability (s vs -s) is irrelevant
/// because the launcher authorises on the signed CONTENT, never on signature identity.</para>
///
/// <para><b>Why the raw bytes and not canonical JSON.</b> The signature covers <c>manifest.json</c>
/// byte for byte, exactly as served. Canonicalising first would mean a second JSON implementation on
/// each side of the fence, and the day the signer's normaliser and the launcher's normaliser disagree
/// about key order, escapes or number formatting, the launcher rejects a legitimate manifest — or
/// worse, accepts two different documents under one signature. Bytes have no such ambiguity.</para>
///
/// <para><b>Fail-closed.</b> A missing key, an unparsable key, a missing signature file, a malformed
/// signature: every one of them is a refusal, never a fallback to "then without a check". The type is
/// immutable and takes its key through the constructor so tests can drive it with a freshly generated
/// key pair and no network at all.</para>
/// </summary>
public sealed class ManifestSignature
{
    /// <summary>
    /// The release public keys, each base64 of a DER SubjectPublicKeyInfo, produced by
    /// <c>deploy/sign-manifest.py --genkey</c>. EMPTY until the owner generates the release key pair
    /// and pastes the public half here.
    ///
    /// <para><b>Why a list and not one key.</b> Rotation. A single embedded key means the day it is
    /// replaced, every launcher already in the field stops accepting updates — permanently, because the
    /// only thing that could tell them about the new key is an update. Carrying the old and the new key
    /// together for one release cycle gives installed clients a way across; the old entry is dropped
    /// once enough of the field has moved. A signature is valid if ANY listed key carries it, which is
    /// the intended transition semantics and no weaker than one key: every entry here is a key we
    /// deliberately trust.</para>
    ///
    /// <para>🔴 As long as this is empty, every launcher update is refused (fail-closed, by design) —
    /// so this constant and a published <c>manifest.json.sig</c> must land TOGETHER, before a build
    /// carrying this gate reaches players. The private halves never enter this repo, never a server:
    /// they live in rbw/Bitwarden and are used offline.</para>
    /// </summary>
    public static readonly string[] EmbeddedPublicKeysBase64 = [
        // Stonetavern release key, generated 2026-07-27. Private half offline (rbw), never in
        // this repository and never on a server. A second entry is added BEFORE a rotation, so
        // launchers in the field accept both keys during the changeover and none is locked out.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEC5Z9C8Vrq+NFaym9G/9E+OkxTPpKDH1nASZDWAOHaCriQuE2tG+EV88hJp+W6qpLmLQ129M0SLHvbjBtHFNQ+w==",
    ];

    /// <summary>P-256 r||s, fixed width. Any other length is a malformed signature, not a variant.</summary>
    private const int P256SignatureLength = 64;

    /// <summary>Guard against a hostile "signature file" of arbitrary size; the real one is 88 chars.</summary>
    private const int MaxSignatureFileChars = 4096;

    private readonly List<byte[]> _publicKeys = [];
    private readonly string _keyProblem;

    /// <param name="publicKeysBase64">Base64 of DER SubjectPublicKeyInfo blobs holding EC P-256 public
    /// keys. Entries that are null, empty or unusable are dropped with a recorded reason; if NONE
    /// survive, this instance permanently refuses — that is the intended behaviour, not an error to be
    /// worked around.</param>
    public ManifestSignature(params string?[] publicKeysBase64)
    {
        var problems = new List<string>();
        foreach (var candidate in publicKeysBase64)
        {
            var parsed = TryParseKey(candidate, out var problem);
            if (parsed is not null)
                _publicKeys.Add(parsed);
            else
                problems.Add(problem);
        }

        _keyProblem = _publicKeys.Count > 0
            ? ""
            : problems.Count == 0
                ? "no embedded manifest public key (ManifestSignature.EmbeddedPublicKeysBase64 is empty)"
                : string.Join("; ", problems);
    }

    /// <summary>Returns the SPKI bytes of a usable P-256 key, or null plus the reason it is not one.
    /// Validation happens once, at construction, so an unusable key is a known state instead of a
    /// surprise on the first update check.</summary>
    private static byte[]? TryParseKey(string? publicKeyBase64, out string problem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            problem = "empty manifest public key entry";
            return null;
        }

        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(publicKeyBase64.Trim());
        }
        catch (FormatException)
        {
            problem = "manifest public key is not valid base64";
            return null;
        }

        // The probe instance is thrown away: ECDsa is not documented as thread-safe, and verification
        // is rare enough that a fresh instance per check costs nothing.
        try
        {
            using var probe = ECDsa.Create();
            probe.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length)
            {
                problem = "manifest public key has trailing bytes";
                return null;
            }
            if (probe.KeySize != 256)
            {
                problem = $"manifest public key is {probe.KeySize} bit, expected 256 (P-256)";
                return null;
            }
        }
        catch (CryptographicException ex)
        {
            problem = $"manifest public key unreadable: {ex.Message}";
            return null;
        }

        problem = "";
        return spki;
    }

    /// <summary>The verifier the shipped launcher uses, bound to <see cref="EmbeddedPublicKeysBase64"/>.</summary>
    public static ManifestSignature Embedded { get; } = new(EmbeddedPublicKeysBase64);

    /// <summary>False when no usable key is embedded — every <see cref="Verify"/> then refuses.</summary>
    public bool HasUsableKey => _publicKeys.Count > 0;

    /// <summary>Empty when the key is usable, otherwise the reason it is not.</summary>
    public string KeyProblem => _keyProblem;

    /// <summary>
    /// True only if <paramref name="signatureFileContent"/> is a valid signature of exactly
    /// <paramref name="manifestBytes"/> under the embedded key.
    /// </summary>
    /// <param name="manifestBytes">The manifest EXACTLY as served — no re-serialisation, no trimming.</param>
    /// <param name="signatureFileContent">Content of <c>manifest.json.sig</c>: base64 of the 64-byte
    /// P-256 r||s signature. Surrounding whitespace and line breaks are tolerated because text files
    /// grow trailing newlines in transit; nothing else is.</param>
    public SignatureVerdict Verify(ReadOnlySpan<byte> manifestBytes, string? signatureFileContent)
    {
        if (_publicKeys.Count == 0)
            return SignatureVerdict.Fail(_keyProblem);

        if (string.IsNullOrWhiteSpace(signatureFileContent))
            return SignatureVerdict.Fail("signature file missing or empty");

        if (signatureFileContent.Length > MaxSignatureFileChars)
            return SignatureVerdict.Fail("signature file implausibly large");

        // Strip only whitespace: a .sig that travelled through a text editor or an HTTP server may
        // carry \r\n or a trailing newline. Anything else stays in and makes the base64 decode fail.
        var compact = StripWhitespace(signatureFileContent);
        Span<byte> signature = stackalloc byte[P256SignatureLength];
        if (!Convert.TryFromBase64String(compact, signature, out var written))
            return SignatureVerdict.Fail("signature is not base64 of a 64-byte P-256 signature");
        if (written != P256SignatureLength)
            return SignatureVerdict.Fail($"signature is {written} bytes, expected {P256SignatureLength}");

        // Any listed key may carry the signature — that is what makes a key rotation survivable for
        // clients already in the field (see EmbeddedPublicKeysBase64). Trying them in order is fine:
        // there are one or two, and a wrong key simply does not verify.
        try
        {
            foreach (var spki in _publicKeys)
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(spki, out _);
                if (key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    return SignatureVerdict.Valid;
            }
            return SignatureVerdict.Fail("signature does not match the manifest bytes under any trusted key");
        }
        catch (CryptographicException ex)
        {
            return SignatureVerdict.Fail($"signature check failed: {ex.Message}");
        }
    }

    private static string StripWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Supplies the manifest the update path is allowed to act on — or nothing at all. Everything it
/// returns has been proven to come from the release key; a caller therefore never has to ask whether
/// the data in its hand is trustworthy, because untrustworthy data never leaves this seam.
/// </summary>
public interface IManifestSignatureGate
{
    /// <summary>
    /// Fetches <c>manifest.json</c> and its detached <c>manifest.json.sig</c>, verifies the signature
    /// over the exact bytes received, runs the release policy (anti-rollback / expiry / channel) on the
    /// manifest parsed FROM THOSE VERIFIED BYTES, and returns it.
    /// Returns null on any failure — offline, missing signature, bad signature, unparsable manifest,
    /// replayed or expired or wrong-channel release. Null always means "do nothing", never "proceed
    /// unchecked".
    /// </summary>
    Task<ServerManifest?> AcquireVerifiedAsync(CancellationToken ct = default);
}

/// <summary>
/// HTTP implementation of <see cref="IManifestSignatureGate"/>.
///
/// <para><b>Why this fetches the manifest a second time</b> instead of verifying the copy
/// <see cref="IManifestService"/> already downloaded: a signature covers bytes, and by the time
/// <c>ManifestService</c> hands out a <see cref="ServerManifest"/> the bytes are gone. Re-fetching is
/// a few kilobytes and buys the property that matters — the object the update path acts on is parsed
/// from the very bytes that were verified, so there is no window between "checked" and "used" in
/// which a different document could be substituted. Folding the check into the manifest fetch itself
/// is the better long-term shape and is left as a handoff, not done here, because that file is owned
/// by another session.</para>
/// </summary>
public sealed class ManifestSignatureGate : IManifestSignatureGate
{
    /// <summary>The detached signature sits next to the manifest under the same name plus this suffix.</summary>
    public const string SignatureSuffix = ".sig";

    /// <summary>A manifest is kilobytes. Reading a hostile multi-gigabyte body into memory before we
    /// have verified anything would be a denial-of-service the signature could never prevent.</summary>
    private const int MaxManifestBytes = 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IConfigService _config;
    private readonly ManifestSignature _signature;
    private readonly ManifestReleasePolicy _policy;
    private readonly Serilog.ILogger _log;

    public ManifestSignatureGate(HttpClient httpClient, IConfigService config,
        ManifestSignature signature, ManifestReleasePolicy policy, Serilog.ILogger log)
    {
        _httpClient = httpClient;
        _config = config;
        _signature = signature;
        _policy = policy;
        _log = log;
    }

    public async Task<ServerManifest?> AcquireVerifiedAsync(CancellationToken ct = default)
    {
        var url = _config.Load().ManifestUrl;

        // Simple-mode profile (custom server, no manifest): nothing to verify and nothing to update.
        // This is not a refusal, so it must not be logged as one.
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!ManifestService.IsValidManifestUrl(url))
        {
            _log.Error("Manifest-Signaturprüfung übersprungen: {Url} ist keine absolute http(s)-Adresse", url);
            return null;
        }

        var signatureUrl = url.Trim() + SignatureSuffix;

        try
        {
            var manifestBytes = await FetchCappedAsync(url.Trim(), ct);
            if (manifestBytes is null)
                return null;

            var signatureText = await FetchSignatureAsync(signatureUrl, ct);
            if (signatureText is null)
            {
                _log.Error(
                    "Kein Manifest-Signaturfile unter {Url} — Launcher-Update wird verweigert (fail-closed)",
                    signatureUrl);
                return null;
            }

            var verdict = _signature.Verify(manifestBytes, signatureText);
            if (!verdict.Ok)
            {
                _log.Error("Manifest-Signatur ungültig ({Reason}) — Launcher-Update wird verweigert", verdict.Reason);
                return null;
            }

            // Parse the VERIFIED bytes. Same options as ManifestService so a manifest that works there
            // works here; a parse failure after a valid signature means we signed something malformed.
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ServerManifest>(manifestBytes,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null)
            {
                _log.Error("Signiertes Manifest ließ sich nicht lesen — Launcher-Update wird verweigert");
                return null;
            }

            // Origin is settled; currency is not. The release policy runs on THIS object — parsed from
            // the bytes the signature covered — so anti-rollback, expiry and channel are decided on the
            // same document that was authenticated, never on a second copy fetched a second time.
            var release = _policy.Admit(manifest);
            if (!release.Ok)
            {
                _log.Error("Signiertes Manifest abgelehnt: {Reason} — kein Launcher-Update", release.Reason);
                return null;
            }

            _log.Information("Manifest-Signatur geprüft und gültig ({Bytes} Bytes)", manifestBytes.Length);
            return manifest;
        }
        // A caller cancel must propagate; a timeout arrives as the same type but means "the server is
        // slow" and is simply "no verified manifest today". Same split as ManifestService.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _log.Warning("Manifest-Signaturprüfung: Zeitüberschreitung — kein Launcher-Update");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _log.Warning(ex, "Manifest-Signaturprüfung: Server nicht erreichbar — kein Launcher-Update");
            return null;
        }
        catch (System.Text.Json.JsonException ex)
        {
            _log.Error(ex, "Signiertes Manifest war kein gültiges JSON — Launcher-Update wird verweigert");
            return null;
        }
    }

    /// <summary>Body of <paramref name="url"/>, or null when the server answered with an error status
    /// or an implausibly large body.</summary>
    private async Task<byte[]?> FetchCappedAsync(string url, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.Error("Manifest {Url} antwortete {Status} — Launcher-Update wird verweigert",
                url, (int)response.StatusCode);
            return null;
        }
        if (response.Content.Headers.ContentLength > MaxManifestBytes)
        {
            _log.Error("Manifest {Url} ist größer als {Max} Bytes — verweigert", url, MaxManifestBytes);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxManifestBytes)
            {
                _log.Error("Manifest {Url} überschreitet {Max} Bytes beim Lesen — verweigert", url, MaxManifestBytes);
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Content of the detached signature file, or null when it is absent/unreachable.
    /// A 404 here is the ordinary "server has not published a signature yet" case — it is an error for
    /// the update path (fail-closed) but must not throw.</summary>
    private async Task<string?> FetchSignatureAsync(string url, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        var bytes = await FetchBodyCappedAsync(response, ct);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]?> FetchBodyCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxManifestBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxManifestBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
