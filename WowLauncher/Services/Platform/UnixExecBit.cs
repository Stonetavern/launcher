namespace WowLauncher.Services.Platform;

/// <summary>
/// Keeps executables executable on Linux and macOS.
///
/// <para><b>Why (E2E 2026-09-24, Modern Linux 1.4.3 → 1.4.4).</b> The per-file patcher downloads a
/// replacement into a fresh file, which gets the default mode 0644. <c>Hermes/linux/JimsProxy</c>
/// lost its execute bit that way, and the next Play failed with "Permission denied": the update meant
/// to fix the game would have stopped every Linux player from starting it.</para>
/// </summary>
public static class UnixExecBit
{
    private const UnixFileMode AnyExecute =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>The file's mode, or null on Windows or when it does not exist.</summary>
    public static UnixFileMode? ModeOf(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        try { return File.GetUnixFileMode(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>True when the file starts like something the OS runs directly: an ELF binary, a
    /// Mach-O binary or a script with a <c>#!</c> line.</summary>
    public static bool LooksExecutable(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[4];
            using var fs = File.OpenRead(path);
            var n = fs.Read(head);
            if (n >= 2 && head[0] == (byte)'#' && head[1] == (byte)'!') return true;
            if (n < 4) return false;
            var magic = BitConverter.ToUInt32(head);
            return (head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
                   || magic is 0xFEEDFACF or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>After a file was replaced: give it execute bits when the file it replaced had them,
    /// or when its content is an executable. Read bits decide who gets execute. Returns true when
    /// the mode was changed. No-op on Windows.</summary>
    public static bool Restore(string path, UnixFileMode? previousMode)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        var wasExecutable = previousMode is { } old && (old & AnyExecute) != 0;
        if (!wasExecutable && !LooksExecutable(path)) return false;
        return Ensure(path);
    }

    /// <summary>Adds execute wherever read is set, if no execute bit is set yet. Returns true when it
    /// changed the mode.</summary>
    public static bool Ensure(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & AnyExecute) != 0) return false;
            var add = UnixFileMode.UserExecute;
            if ((mode & UnixFileMode.GroupRead) != 0) add |= UnixFileMode.GroupExecute;
            if ((mode & UnixFileMode.OtherRead) != 0) add |= UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode | add);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
