using System;
using System.Threading;
using System.Threading.Tasks;

namespace WowLauncher.Startup;

/// <summary>How a sign-in attempt ended, as the login surface tells it apart (Spec §6).</summary>
public enum AuthResultKind
{
    /// <summary>Accepted. The sigil pulses once, then Phase 3.</summary>
    Success,

    /// <summary>The server said no (wrong account name or password). Card shake, inline error,
    /// focus back in the password field.</summary>
    Rejected,

    /// <summary>No answer within <see cref="AuthPolicy.Timeout"/>. Own message, offline path offered.</summary>
    Timeout,

    /// <summary>Could not reach the auth endpoint at all. Same surface as Timeout.</summary>
    Unavailable,
}

/// <summary>The outcome of one sign-in. <paramref name="Message"/> is a localized, VOICE-compliant
/// English line safe to show under the field; null on success.</summary>
public sealed record AuthResult(AuthResultKind Kind, string? AccountName, string? Message)
{
    public static AuthResult Success(string accountName) => new(AuthResultKind.Success, accountName, null);
    public static AuthResult Rejected(string message) => new(AuthResultKind.Rejected, null, message);
    public static AuthResult Timeout(string message) => new(AuthResultKind.Timeout, null, message);
    public static AuthResult Unavailable(string message) => new(AuthResultKind.Unavailable, null, message);
}

/// <summary>
/// What Phase 2 talks to. Deliberately narrow: one call, one result, no session, no token.
///
/// <para>Spec §12.1 A is decided: the player path uses <see cref="LauncherAuthGateway"/> over the
/// launcher sign-in (<see cref="Services.ILauncherAuthService"/>, bearer token for friends/armory).
/// The fake below stays for the QA render harness and the tests; it is not reachable from the player
/// path.</para>
/// </summary>
public interface IAuthGateway
{
    Task<AuthResult> SignInAsync(string username, string password, CancellationToken ct);
}

/// <summary>Timing rules of Phase 2 that are not motion (Spec §6).</summary>
public static class AuthPolicy
{
    /// <summary>"Timeout: nach 10 s Abbruch mit eigener Meldung, die den Offline-Pfad anbietet."</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
}

/// <summary>
/// A sign-in that answers whatever the config says, so every Phase-2 surface (spinner, shake, inline
/// error, timeout sentence, success pulse) can be looked at today without a server and without §12.1.
///
/// <para>Modes (config <c>LauncherShellFakeAuth</c>, case-insensitive): <c>success</c> (default),
/// <c>reject</c>, <c>timeout</c> (never answers; the ViewModel's 10 s cut-off fires),
/// <c>unavailable</c>. Any other value is treated as <c>success</c> and logged.</para>
///
/// <para>Never stores, never logs the password. It does not even look at it.</para>
/// </summary>
public sealed class FakeAuthGateway : IAuthGateway
{
    public const string SuccessMode = "success";
    public const string RejectMode = "reject";
    public const string TimeoutMode = "timeout";
    public const string UnavailableMode = "unavailable";

    /// <summary>How long the stub takes to answer on an interactive run: long enough that the button
    /// spinner and the read-only fields can be seen, short enough not to feel like a real wait.</summary>
    public static readonly TimeSpan DefaultLatency = TimeSpan.FromMilliseconds(700);

    private readonly string _mode;
    private readonly TimeSpan _latency;
    private readonly Func<string, string> _text;

    /// <param name="mode">See the class remarks.</param>
    /// <param name="latency">How long a Success/Reject/Unavailable answer takes, so the spinner is
    /// visible for a human. Tests pass zero.</param>
    /// <param name="text">Localizer for the messages, so this class carries no Loc dependency in
    /// tests. Default: <c>Localization.Loc.T</c>.</param>
    public FakeAuthGateway(string? mode, TimeSpan latency, Func<string, string>? text = null)
    {
        _mode = Normalize(mode);
        _latency = latency;
        _text = text ?? Localization.Loc.T;
    }

    public string Mode => _mode;

    public static string Normalize(string? mode)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant();
        return m is RejectMode or TimeoutMode or UnavailableMode ? m : SuccessMode;
    }

    public async Task<AuthResult> SignInAsync(string username, string password, CancellationToken ct)
    {
        if (_mode == TimeoutMode)
        {
            // Never answers. The caller's cut-off is the only way out, and it must be.
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }

        if (_latency > TimeSpan.Zero)
            await Task.Delay(_latency, ct).ConfigureAwait(false);

        return _mode switch
        {
            RejectMode => AuthResult.Rejected(_text("Login_Error_Invalid")),
            UnavailableMode => AuthResult.Unavailable(_text("Login_Error_Unavailable")),
            _ => AuthResult.Success(username.Trim()),
        };
    }
}
