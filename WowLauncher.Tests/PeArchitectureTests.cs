using System.IO;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The PE architecture reader that backs the fail-closed bitness check. Crafted headers prove the walk
/// (MZ → e_lfanew → PE signature → COFF machine) and, just as important, that anything malformed maps to
/// <see cref="PeArch.Unknown"/> rather than throwing or guessing — a file we cannot read as a PE is not
/// something the launcher will start.
/// </summary>
public sealed class PeArchitectureTests
{
    /// <summary>Build a minimal but valid PE header carrying <paramref name="machine"/> in the COFF
    /// header, with the PE header placed at offset <paramref name="peOffset"/>.</summary>
    private static byte[] MakePe(int machine, int peOffset = 0x80)
    {
        var buf = new byte[peOffset + 8];
        buf[0] = (byte)'M';
        buf[1] = (byte)'Z';
        // e_lfanew at 0x3C, little-endian.
        buf[0x3C] = (byte)(peOffset & 0xFF);
        buf[0x3D] = (byte)((peOffset >> 8) & 0xFF);
        buf[0x3E] = (byte)((peOffset >> 16) & 0xFF);
        buf[0x3F] = (byte)((peOffset >> 24) & 0xFF);
        // "PE\0\0" at peOffset.
        buf[peOffset + 0] = (byte)'P';
        buf[peOffset + 1] = (byte)'E';
        buf[peOffset + 2] = 0;
        buf[peOffset + 3] = 0;
        // Machine, LE, at peOffset + 4.
        buf[peOffset + 4] = (byte)(machine & 0xFF);
        buf[peOffset + 5] = (byte)((machine >> 8) & 0xFF);
        return buf;
    }

    private static PeArch Read(byte[] bytes) => PeArchitecture.Read(new MemoryStream(bytes));

    [Fact]
    public void Amd64_IsReadAsX64() => Assert.Equal(PeArch.X64, Read(MakePe(0x8664)));

    [Fact]
    public void I386_IsReadAsX86() => Assert.Equal(PeArch.X86, Read(MakePe(0x014c)));

    [Fact]
    public void Arm64_IsReadAsArm64() => Assert.Equal(PeArch.Arm64, Read(MakePe(0xAA64)));

    [Fact]
    public void AnUnknownMachine_IsUnknown() => Assert.Equal(PeArch.Unknown, Read(MakePe(0x1234)));

    [Fact]
    public void ANonPeFile_IsUnknown() =>
        Assert.Equal(PeArch.Unknown, Read([0x00, 0x01, 0x02, 0x03, 0x04, 0x05]));

    [Fact]
    public void AnMzStubTooShortForThePeHeader_IsUnknown_NotAThrow()
    {
        // "MZ" plus a bogus e_lfanew pointing past the end of the buffer.
        var buf = new byte[64];
        buf[0] = (byte)'M';
        buf[1] = (byte)'Z';
        buf[0x3C] = 0xFF; // e_lfanew = 0xFF, well past the 64-byte buffer
        Assert.Equal(PeArch.Unknown, Read(buf));
    }

    [Fact]
    public void AnEmptyFile_IsUnknown() => Assert.Equal(PeArch.Unknown, Read([]));

    [Fact]
    public void ReadFromAMissingPath_IsUnknown_NotAThrow() =>
        Assert.Equal(PeArch.Unknown, PeArchitecture.Read(Path.Combine(Path.GetTempPath(), "no-such-" + System.Guid.NewGuid().ToString("N") + ".exe")));
}
