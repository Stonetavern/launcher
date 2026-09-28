namespace WowLauncher.Services.Platform;

using System.Net.Sockets;
using System.Text;

/// <summary>
/// One launcher per player: a second start brings the running one to the front and quits.
///
/// <para><b>Why.</b> The launcher hides in the tray while the game runs, and a player who wants it back
/// usually starts it again instead of finding the tray icon. Until 2026-09-28 there was no lock at all
/// (see <see cref="ProxyPidFile"/>): the second window fought the first over the config and the realm
/// proxy. The Stonetavern folder made it more likely (an old download forwards to the folder's launcher
/// while that one already runs; two setup windows can be open at once, E2E 2026-09-28).</para>
///
/// <para><b>How.</b> The lock is an exclusively opened file (Windows: share mode; Unix: .NET takes an
/// flock for <c>FileShare.None</c>), released by the OS when the process ends, crash included, so there
/// is no stale lock to clean up. The "come to the front" message goes over a Unix domain socket (also on
/// Windows 10 1803+) in a per-user folder, named after the lock it belongs to, because .NET's named pipes put their socket at a path they
/// choose themselves, with umask permissions before .NET 11. A socket file left by a crash is removed by
/// whoever holds the lock: only the lock holder may own it.</para>
///
/// <para><b>Fails open.</b> If the first launcher does not answer (hung, or no socket), the second one
/// starts as it always did. A lock that could keep a player out of their launcher is worse than two
/// windows.</para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowMessage = "show";
    private const int MaxSocketPath = 100;   // sun_path is 104 (macOS) or 108 bytes; keep a margin

    private readonly string _lockPath;
    private readonly string? _socketPath;
    private readonly object _gate = new();
    private FileStream? _lock;
    private Socket? _server;
    private Action? _onShow;

    /// <summary>The instance of this process, once <see cref="TryAcquire"/> ran. Null in QA and tools.</summary>
    public static SingleInstance? Current { get; set; }

    public SingleInstance(string lockPath, string? socketPath)
    {
        _lockPath = lockPath;
        _socketPath = socketPath is { Length: > 0 and <= MaxSocketPath } ? socketPath : null;
    }

    /// <summary>The lock beside the launcher state, the socket in a per-user folder that is short enough
    /// for a socket path: <c>$XDG_RUNTIME_DIR</c> on Linux (else the state folder, never the shared
    /// <c>/tmp</c> where another user could take the name first), the per-user temp folder on Windows
    /// and macOS.</summary>
    public static SingleInstance ForCurrentUser(string stateDir)
    {
        string socketDir;
        if (OperatingSystem.IsLinux())
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            socketDir = !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime) ? runtime : stateDir;
        }
        else
        {
            socketDir = Path.GetTempPath();
        }
        var lockPath = Path.Combine(stateDir, "launcher.instance.lock");
        return new SingleInstance(lockPath, Path.Combine(socketDir, SocketNameFor(lockPath)));
    }

    /// <summary>The socket belongs to one lock, so its name comes from the lock's path. With one fixed
    /// name, a launcher with a different state folder (a second home, a portable copy, a test) took the
    /// other's lock-free socket over: "only the lock holder may own it" did not hold, and later starts
    /// were sent to the wrong window.</summary>
    internal static string SocketNameFor(string lockPath)
    {
        var full = Path.GetFullPath(lockPath);
        if (!OperatingSystem.IsLinux()) full = full.ToLowerInvariant();   // case-insensitive file systems
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return "stonetavern-launcher-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant() + ".sock";
    }

    public bool HoldsLock
    {
        get { lock (_gate) return _lock is not null; }
    }

    /// <summary>True when this process is now the one launcher. False when another holds the lock.</summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_lock is not null) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
                _lock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                return false;   // held by the running launcher
            }
            catch (UnauthorizedAccessException)
            {
                return true;    // cannot lock at all: behave as before, never keep the player out
            }
        }
    }

    /// <summary>Accept "come to the front" from later starts; <paramref name="onShow"/> runs on a
    /// background thread (post it to the UI thread). Only while this process holds the lock.</summary>
    public void Listen(Action onShow)
    {
        lock (_gate)
        {
            _onShow = onShow;
            if (_lock is null || _server is not null || _socketPath is null) return;
            try
            {
                if (File.Exists(_socketPath)) File.Delete(_socketPath);   // left by a crash; we hold the lock
                var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                server.Bind(new UnixDomainSocketEndPoint(_socketPath));
                server.Listen(4);
                _server = server;
                _ = Task.Run(() => AcceptLoop(server));
            }
            catch (Exception)
            {
                _server = null;   // no front-bringing, the lock still holds
            }
        }
    }

    private async Task AcceptLoop(Socket server)
    {
        while (true)
        {
            Socket client;
            try { client = await server.AcceptAsync().ConfigureAwait(false); }
            catch (Exception) { return; }   // closed by Release/Dispose
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var buffer = new byte[16];
                        var n = await client.ReceiveAsync(buffer, SocketFlags.None, cts.Token).ConfigureAwait(false);
                        if (Encoding.ASCII.GetString(buffer, 0, n).Trim() != ShowMessage) return;
                        Action? show;
                        lock (_gate) show = _onShow;
                        show?.Invoke();
                        await client.SendAsync("ok\n"u8.ToArray(), SocketFlags.None, cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // a start that gave up waiting; nothing to do
                    }
                }
            });
        }
    }

    /// <summary>Second start: ask the running launcher to come to the front. True only when it said so.</summary>
    public bool SignalFirst(TimeSpan timeout)
    {
        if (_socketPath is null || !File.Exists(_socketPath)) return false;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cts.Token).AsTask().GetAwaiter().GetResult();
            socket.SendAsync(Encoding.ASCII.GetBytes(ShowMessage + "\n"), SocketFlags.None, cts.Token).AsTask().GetAwaiter().GetResult();
            var buffer = new byte[8];
            var n = socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token).AsTask().GetAwaiter().GetResult();
            return Encoding.ASCII.GetString(buffer, 0, n).StartsWith("ok", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Hand the role on: stop listening and let go of the lock, right before starting the
    /// launcher that takes over (the Stonetavern-folder setup). Otherwise that launcher would find the
    /// lock held, bring THIS window forward and quit.</summary>
    public void Release()
    {
        lock (_gate)
        {
            try { _server?.Dispose(); } catch (Exception) { }
            _server = null;
            try { if (_socketPath is not null && File.Exists(_socketPath)) File.Delete(_socketPath); } catch (Exception) { }
            try { _lock?.Dispose(); } catch (Exception) { }
            _lock = null;
        }
    }

    /// <summary>Take the role back after a hand-on that did not happen (start failed, setup rolled back).
    /// A successor that started and still runs keeps the lock; then this one stays without.</summary>
    public void Reacquire()
    {
        Action? show;
        lock (_gate) show = _onShow;
        if (TryAcquire() && show is not null) Listen(show);
    }

    public void Dispose() => Release();
}
