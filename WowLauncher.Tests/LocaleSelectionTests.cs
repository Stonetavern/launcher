using System.Collections.Generic;
using System.Linq;
using WowLauncher.Models;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Keeping the picked language ON the picker while its list is still arriving.
///
/// <para>The menu is read from the installation and from the manifest, so the first frame offers
/// English only and the real list lands a moment later. A ComboBox drops a selection that is not in
/// its list and writes the null back through the binding - so without this, a player whose saved
/// language is German gets it cleared before the menu arrives, and the box then shows NOTHING while
/// the client is German. Seen in the Linux end-to-end run on 2026-08-04, and only in the rendered
/// window: every value in the view model was correct.</para>
/// </summary>
public sealed class LocaleSelectionTests
{
    private static IReadOnlyList<LocaleInfo> Menu(params string[] codes) =>
        codes.Select(ClientLocales.FromCode).ToList();

    /// <summary>The case from the end-to-end run: the picker cleared the selection, the real menu
    /// arrives, and the saved language has to come back.</summary>
    [Fact]
    public void AClearedSelection_ComesBackFromTheSavedLanguage()
    {
        var repaired = LocaleSelection.Repair(Menu("enUS", "deDE", "frFR"), currentCode: null, savedCode: "deDE");

        Assert.Equal("deDE", repaired!.Code);
    }

    [Fact]
    public void AValidSelection_IsLeftAlone()
    {
        var repaired = LocaleSelection.Repair(Menu("enUS", "deDE"), currentCode: "deDE", savedCode: "enUS");

        Assert.Equal("deDE", repaired!.Code);
    }

    /// <summary>Switching to a client that cannot deliver the picked language falls back to English
    /// rather than to nothing.</summary>
    [Fact]
    public void ASelectionTheNewMenuCannotOffer_FallsBackToEnglish()
    {
        var repaired = LocaleSelection.Repair(Menu("enUS", "frFR"), currentCode: "deDE", savedCode: "deDE");

        Assert.Equal("enUS", repaired!.Code);
    }

    /// <summary>An empty box tells the player nothing at all, so a menu without English still gets a
    /// selection - the first entry.</summary>
    [Fact]
    public void AMenuWithoutEnglish_StillGetsASelection()
    {
        var repaired = LocaleSelection.Repair(Menu("frFR", "ruRU"), currentCode: "deDE", savedCode: "deDE");

        Assert.Equal("frFR", repaired!.Code);
    }

    /// <summary>Nothing to select from is the one case that stays null: inventing a language for an
    /// empty menu would put a name on a control that offers nothing.</summary>
    [Fact]
    public void AnEmptyMenu_SelectsNothing() =>
        Assert.Null(LocaleSelection.Repair([], currentCode: "deDE", savedCode: "deDE"));

    [Fact]
    public void TheSavedLanguageOnlyCountsWhenNothingIsSelected()
    {
        var repaired = LocaleSelection.Repair(Menu("enUS", "deDE", "frFR"), currentCode: "frFR", savedCode: "deDE");

        Assert.Equal("frFR", repaired!.Code);
    }
}
