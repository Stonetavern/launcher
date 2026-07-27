using WowLauncher.Localization;

namespace WowLauncher.Models;

/// <summary>
/// Why a download failed — lets the UI show a specific, self-diagnosable message
/// instead of a generic "download failed" (Hermes §1.4 error classification).
/// </summary>
public enum DownloadFailure
{
    None,
    /// <summary>No connection / DNS / timeout — the user's side.</summary>
    Network,
    /// <summary>Server reached but returned an error status (5xx/4xx) — the server's side.</summary>
    ServerError,
    /// <summary>Local disk write/space/permission failure.</summary>
    DiskIo,
    /// <summary>Download finished but SHA256 did not match — corruption or tampering.</summary>
    HashMismatch,
    /// <summary>Cancelled by the user / app shutdown.</summary>
    Cancelled,
}

/// <summary>Outcome of a download attempt, carrying the failure class + a human detail line.</summary>
public sealed record DownloadResult(bool Ok, DownloadFailure Failure = DownloadFailure.None, string? Detail = null)
{
    public static DownloadResult Success => new(true);
    public static DownloadResult Fail(DownloadFailure f, string? detail = null) => new(false, f, detail);

    /// <summary>A short, user-facing line per failure class, in the launcher's UI language
    /// (self-diagnosis). Resolved on read, so a language switch reflows an error already on screen.</summary>
    public string UserMessage => Failure switch
    {
        DownloadFailure.Network => Loc.T("Download_Fail_Network"),
        DownloadFailure.ServerError => Loc.T("Download_Fail_Server"),
        DownloadFailure.DiskIo => Loc.T("Download_Fail_Disk"),
        DownloadFailure.HashMismatch => Loc.T("Download_Fail_Hash"),
        DownloadFailure.Cancelled => Loc.T("Download_Fail_Cancelled"),
        _ => Loc.T("Download_Fail_Generic"),
    };
}
