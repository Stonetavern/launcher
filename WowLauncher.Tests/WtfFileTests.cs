using System;
using System.IO;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The duplicate-key rule for <c>.wtf</c> files (Codex review 2026-08-09, finding 1).
///
/// <para>A <c>Config.wtf</c> can legitimately end up with two <c>SET realmList</c> lines: one written
/// by the package, one left over from an older tool or an earlier launcher. The writer used to rewrite
/// the FIRST match and the readback used to accept the FIRST match, so such a file passed as "written
/// and confirmed" while a stale address survived further down — and if the 1.12.1 client honours the
/// last definition, the player lands on the wrong realm with a green launcher. Which line the client
/// obeys is not documented anywhere we can rely on, so the rule is: after a write the file contains
/// exactly ONE line for the key, and a file that still disagrees with itself is not readable back.</para>
///
/// <para>This is the same rule already established for <c>portal</c> in
/// <c>WowLauncher.Services.Platform.WtfConfigWriter</c> (Codex round 2026-07-27) — one semantics, not
/// two.</para>
/// </summary>
public sealed class WtfFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mechagon-wtffile-" + Guid.NewGuid().ToString("N"));

    public WtfFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_dir, "Config.wtf");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void SetVar_RemovesLaterDuplicates_NotJustTheFirstMatch()
    {
        var path = Write(
            "SET realmList \"play.stonetavern.app\"",
            "SET locale \"deDE\"",
            "SET realmList \"old.example.invalid\"");

        WtfFile.SetVar(path, "realmList", "play.example.invalid:3725");

        var text = File.ReadAllText(path);
        Assert.Equal(
            "SET realmList \"play.example.invalid:3725\"\nSET locale \"deDE\"\n", text);
        Assert.DoesNotContain("old.example.invalid", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SetVar_AlsoRemovesADuplicateWrittenWithATab()
    {
        // The old matcher required the literal "SET " with an ASCII space, so a tab-separated line was
        // invisible to writer and readback alike — exactly the hole closed for `portal` in 2026-07-27.
        var path = Write(
            "SET realmList \"play.stonetavern.app\"",
            "SET\trealmList \"old.example.invalid\"");

        WtfFile.SetVar(path, "realmList", "realm.example.invalid");

        Assert.Equal("SET realmList \"realm.example.invalid\"\n", File.ReadAllText(path));
    }

    [Fact]
    public void SetVar_PreservesOrderAndAppendsAnUnknownKey()
    {
        var path = Write("SET locale \"deDE\"");

        WtfFile.SetVar(path, "realmList", "realm.example.invalid");

        Assert.Equal("SET locale \"deDE\"\nSET realmList \"realm.example.invalid\"\n",
            File.ReadAllText(path));
    }

    [Fact]
    public void ReadVar_RefusesAnAmbiguousFile_InsteadOfTakingTheFirstLine()
    {
        // Nobody wrote through SetVar here — this is the state a foreign tool can leave behind, and the
        // state a failed write leaves behind. Answering "play.stonetavern.app" would be a green light
        // for a file whose last line may be the one that counts.
        var path = Write(
            "SET realmList \"play.stonetavern.app\"",
            "SET realmList \"old.example.invalid\"");

        Assert.Null(WtfFile.ReadVar(path, "realmList"));
        Assert.Equal(2, WtfFile.ReadValues(path, "realmList").Count);
    }

    [Fact]
    public void ReadVar_AcceptsDuplicatesThatAgree()
    {
        // Two identical lines are redundant but not ambiguous: there is nothing for the client to pick
        // wrongly. Refusing here would turn a harmless file into a dead Play button.
        var path = Write(
            "SET realmList \"realm.example.invalid\"",
            "SET realmList \"realm.example.invalid\"");

        Assert.Equal("realm.example.invalid", WtfFile.ReadVar(path, "realmList"));
    }

    [Fact]
    public void ReadVar_ReturnsNullForAMissingFileOrKey()
    {
        Assert.Null(WtfFile.ReadVar(Path.Combine(_dir, "nope.wtf"), "realmList"));
        Assert.Null(WtfFile.ReadVar(Write("SET locale \"deDE\""), "realmList"));
    }

    [Fact]
    public void ReadVar_DoesNotMatchAKeyThatMerelyStartsTheSame()
    {
        // "realmListBackup" is not "realmList"; a prefix match would read a foreign key's value and
        // report the wrong realm as confirmed.
        var path = Write("SET realmListBackup \"old.example.invalid\"");

        Assert.Null(WtfFile.ReadVar(path, "realmList"));
        Assert.Empty(WtfFile.ReadValues(path, "realmList"));
    }
}
