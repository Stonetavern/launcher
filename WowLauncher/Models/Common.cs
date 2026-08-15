namespace WowLauncher.Models;

/// <summary>
/// Server status check result.
/// </summary>
public enum ServerStatus
{
    Unknown,
    Online,
    Offline,
    Error
}

/// <summary>
/// Download progress information.
/// </summary>
public sealed class DownloadProgress
{
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public double Percentage => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100.0 : 0;
    public double SpeedBytesPerSecond { get; init; }
    public string Status { get; init; } = "";

    /// <summary>Set while the download is waiting out a lost connection rather than transferring.
    /// The view needs to tell those apart: a byte counter that stops moving looks like a hang, and a
    /// player who thinks it hung closes the launcher — which is exactly when the recovery would have
    /// worked. Null means "transferring".</summary>
    public RetryWait? Waiting { get; init; }
}

/// <summary>The launcher lost the connection and is going to try again by itself.</summary>
/// <param name="Attempt">Which attempt is coming, 1-based.</param>
/// <param name="Of">How many it will make before giving up.</param>
/// <param name="In">How long until it does.</param>
/// <param name="Reason">What the network said, for the log and the details line.</param>
public sealed record RetryWait(int Attempt, int Of, TimeSpan In, string Reason);
