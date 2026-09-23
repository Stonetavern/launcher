using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WowLauncher.Services.Platform;

/// <summary>
/// Windows: the primary monitor through <c>EnumDisplaySettings</c>. This is the ONE API that is not
/// DPI-virtualised: <c>dmPelsWidth/Height</c> are physical pixels whatever the process's awareness,
/// and <c>dmDisplayFrequency</c> is the mode's real rate (the packages' PowerShell read
/// <c>Screen.PrimaryScreen.Bounds</c> — scaled on any desktop above 100 % — and WMI's
/// <c>CurrentRefreshRate</c>, which hands out 0/1 as placeholders; measured 2026-09-05).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayProbe : IDisplayProbe
{
    private const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private const int AttachedToDesktop = 0x1;
    private const int PrimaryDevice = 0x4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);

    public DisplayProbeResult? Probe()
    {
        // Current mode of the primary device (null device = the calling session's primary).
        var dm = Fresh();
        if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref dm) || dm.dmPelsWidth <= 0 || dm.dmPelsHeight <= 0)
            return null;
        var current = new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);

        // Modes of EVERY monitor attached to the desktop, so a fullscreen game on a secondary
        // screen is judged against that screen's list too (review 2026-09-05, finding 2).
        var modes = new HashSet<DisplayMode>();
        var devices = new List<string?>();
        for (var d = 0; d < 32; d++)
        {
            var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, d, ref dev, 0)) break;
            if ((dev.StateFlags & AttachedToDesktop) != 0) devices.Add(dev.DeviceName);
        }
        if (devices.Count == 0) devices.Add(null);
        foreach (var device in devices)
            for (var i = 0; i < 4096; i++)
            {
                var m = Fresh();
                if (!EnumDisplaySettingsW(device, i, ref m)) break;
                if (m.dmPelsWidth > 0 && m.dmPelsHeight > 0)
                    modes.Add(new DisplayMode(m.dmPelsWidth, m.dmPelsHeight, m.dmDisplayFrequency));
            }
        return new DisplayProbeResult(current, modes.ToList());
    }

    private static DEVMODE Fresh()
    {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        return dm;
    }
}

/// <summary>
/// Linux: <c>xrandr</c> first (the primary output's current mode, every connected output's modes), then
/// <c>kscreen-doctor -j</c> for KDE on Wayland, where xrandr only sees the XWayland view. The same
/// two sources <c>launch.sh</c> uses; the parsing is pure so both formats are proven by test.
/// </summary>
public sealed class LinuxDisplayProbe : IDisplayProbe
{
    private readonly Func<string, string[], string?> _run;

    /// <param name="run">Test seam: run a command with arguments, return stdout or null when it
    /// is missing/failing. Null = run it for real with a short timeout.</param>
    public LinuxDisplayProbe(Func<string, string[], string?>? run = null)
    {
        _run = run ?? RunForReal;
    }

    public DisplayProbeResult? Probe()
    {
        var xr = _run("xrandr", []);
        if (xr is not null && ParseXrandr(xr) is { } fromXrandr) return fromXrandr;
        var ks = _run("kscreen-doctor", ["-j"]);
        if (ks is not null && ParseKscreen(ks) is { } fromKscreen) return fromKscreen;
        return null;
    }

    private static readonly Regex ConnectedLine = new(@"^(\S+) connected( primary)?\b", RegexOptions.Compiled);
    private static readonly Regex ModeLine = new(@"^\s+(\d+)x(\d+)[a-z]?\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RateToken = new(@"(\d+(?:\.\d+)?)([*+]*)", RegexOptions.Compiled);

    /// <summary>The primary output's block (or the first connected one). Current = the rate marked
    /// <c>*</c>; when nothing is marked current (a screen that is off), the preferred <c>+</c>.
    /// Rates are rounded commercially: xrandr says 359.80 for a 360 Hz mode.</summary>
    internal static DisplayProbeResult? ParseXrandr(string output)
    {
        var lines = output.Split('\n');
        var blocks = new List<(bool primary, List<DisplayMode> modes, DisplayMode? current, DisplayMode? preferred)>();
        (bool primary, List<DisplayMode> modes, DisplayMode? current, DisplayMode? preferred)? open = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var head = ConnectedLine.Match(line);
            if (head.Success)
            {
                if (open is { } done) blocks.Add(done);
                open = (head.Groups[2].Success, new List<DisplayMode>(), null, null);
                continue;
            }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                if (open is { } done) blocks.Add(done);
                open = null;
                continue;
            }
            if (open is null) continue;
            var m = ModeLine.Match(line);
            if (!m.Success) continue;
            var w = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var h = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var block = open.Value;
            foreach (Match r in RateToken.Matches(m.Groups[3].Value))
            {
                var hz = (int)Math.Round(double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture),
                    MidpointRounding.AwayFromZero);
                var mode = new DisplayMode(w, h, hz);
                block.modes.Add(mode);
                if (r.Groups[2].Value.Contains('*')) block.current = mode;
                if (r.Groups[2].Value.Contains('+')) block.preferred = mode;
            }
            open = block;
        }
        if (open is { } last) blocks.Add(last);
        if (blocks.Count == 0) return null;
        var chosen = blocks.FirstOrDefault(b => b.primary);
        if (chosen.modes is null) chosen = blocks[0];
        var cur = chosen.current ?? chosen.preferred;
        if (cur is null || chosen.modes.Count == 0) return null;
        // Every connected output's modes: a fullscreen game on a secondary screen is judged
        // against that screen too (review 2026-09-05, finding 2).
        var all = blocks.SelectMany(b => b.modes).Distinct().ToList();
        return new DisplayProbeResult(cur.Value, all);
    }

    internal static DisplayProbeResult? ParseKscreen(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("outputs", out var outputs)) return null;
            JsonElement? best = null;
            var bestScore = -1L;
            var enabled = new List<JsonElement>();
            foreach (var o in outputs.EnumerateArray())
            {
                if (!(o.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True)) continue;
                enabled.Add(o);
                var score = o.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True ? 1L << 40 : 0;
                if (score > bestScore) { bestScore = score; best = o; }
            }
            if (best is null) return null;
            var modes = new List<DisplayMode>();
            DisplayMode? current = null;
            foreach (var output in enabled)
            {
                var isChosen = output.Equals(best.Value);
                var currentId = output.TryGetProperty("currentModeId", out var cid) ? cid.ToString() : null;
                if (!output.TryGetProperty("modes", out var modeArr)) continue;
                foreach (var m in modeArr.EnumerateArray())
                {
                    if (!m.TryGetProperty("size", out var size)) continue;
                    var w = size.GetProperty("width").GetInt32();
                    var h = size.GetProperty("height").GetInt32();
                    var hz = m.TryGetProperty("refreshRate", out var rr)
                        ? (int)Math.Round(rr.GetDouble(), MidpointRounding.AwayFromZero) : 0;
                    var mode = new DisplayMode(w, h, hz);
                    modes.Add(mode);
                    if (isChosen && currentId is not null && m.TryGetProperty("id", out var id) && id.ToString() == currentId)
                        current = mode;
                }
            }
            if (current is null || modes.Count == 0) return null;
            return new DisplayProbeResult(current.Value, modes.Distinct().ToList());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? RunForReal(string command, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { /* best effort */ } return null; }
            return p.ExitCode == 0 ? stdout : null;
        }
        catch (Exception)
        {
            return null;    // not installed, not on PATH, no display: the caller falls through
        }
    }
}
