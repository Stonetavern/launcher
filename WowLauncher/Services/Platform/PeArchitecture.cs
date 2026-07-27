namespace WowLauncher.Services.Platform;

/// <summary>The machine architecture a PE (Windows executable) is built for.</summary>
public enum PeArch
{
    /// <summary>The file could not be read as a PE, or its machine field is one we do not recognise.</summary>
    Unknown,
    X86,   // IMAGE_FILE_MACHINE_I386  (0x014c) — 32-bit
    X64,   // IMAGE_FILE_MACHINE_AMD64 (0x8664) — 64-bit
    Arm64, // IMAGE_FILE_MACHINE_ARM64 (0xAA64)
}

/// <summary>
/// Reads the architecture out of a PE file's COFF header, so a launch can be refused BEFORE starting a
/// process whose bitness does not match the build (Codex review §4a: a 32-bit exe handed to a 64-bit
/// build, or the reverse, is a silent failure — the process starts and then dies or misbehaves in a way
/// that looks like something else). Pure and side-effect-free: it reads a handful of header bytes and
/// returns a value, so it is unit-tested on any OS with crafted headers.
///
/// <para>Layout it walks (all little-endian): <c>MZ</c> at offset 0 → the PE header offset
/// (<c>e_lfanew</c>) at 0x3C → the <c>PE\0\0</c> signature there → the COFF <c>Machine</c> field in the
/// two bytes right after the signature.</para>
/// </summary>
public static class PeArchitecture
{
    private const int MachineI386 = 0x014c;
    private const int MachineAmd64 = 0x8664;
    private const int MachineArm64 = 0xAA64;

    /// <summary>Read the architecture of the PE at <paramref name="path"/>, or <see cref="PeArch.Unknown"/>
    /// when the file is missing, too short, or not a PE. Never throws.</summary>
    public static PeArch Read(string path)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            return Read(stream);
        }
        catch
        {
            return PeArch.Unknown;
        }
    }

    /// <summary>The pure core, against a seekable stream, so a test can hand crafted bytes without a
    /// file. Never throws — any malformed input maps to <see cref="PeArch.Unknown"/>.</summary>
    public static PeArch Read(System.IO.Stream stream)
    {
        try
        {
            if (!stream.CanSeek) return PeArch.Unknown;

            // DOS header: "MZ" magic, then e_lfanew (PE header offset) at 0x3C as a 4-byte LE int.
            if (ReadByteAt(stream, 0) != (byte)'M' || ReadByteAt(stream, 1) != (byte)'Z')
                return PeArch.Unknown;

            var peOffset = ReadUInt32LE(stream, 0x3C);
            if (peOffset == 0 || peOffset > int.MaxValue - 6) return PeArch.Unknown;

            // PE signature "PE\0\0" at e_lfanew.
            if (ReadByteAt(stream, (long)peOffset) != (byte)'P' ||
                ReadByteAt(stream, (long)peOffset + 1) != (byte)'E' ||
                ReadByteAt(stream, (long)peOffset + 2) != 0 ||
                ReadByteAt(stream, (long)peOffset + 3) != 0)
                return PeArch.Unknown;

            // COFF Machine: the 2 bytes right after the signature, LE.
            int machine = ReadUInt16LE(stream, (long)peOffset + 4);
            return machine switch
            {
                MachineAmd64 => PeArch.X64,
                MachineI386 => PeArch.X86,
                MachineArm64 => PeArch.Arm64,
                _ => PeArch.Unknown,
            };
        }
        catch
        {
            return PeArch.Unknown;
        }
    }

    private static int ReadByteAt(System.IO.Stream s, long offset)
    {
        s.Seek(offset, System.IO.SeekOrigin.Begin);
        var b = s.ReadByte();
        if (b < 0) throw new System.IO.EndOfStreamException();
        return b;
    }

    private static uint ReadUInt32LE(System.IO.Stream s, long offset)
    {
        s.Seek(offset, System.IO.SeekOrigin.Begin);
        Span<byte> buf = stackalloc byte[4];
        if (s.Read(buf) != 4) throw new System.IO.EndOfStreamException();
        return (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
    }

    private static int ReadUInt16LE(System.IO.Stream s, long offset)
    {
        s.Seek(offset, System.IO.SeekOrigin.Begin);
        Span<byte> buf = stackalloc byte[2];
        if (s.Read(buf) != 2) throw new System.IO.EndOfStreamException();
        return buf[0] | (buf[1] << 8);
    }
}
