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
}
