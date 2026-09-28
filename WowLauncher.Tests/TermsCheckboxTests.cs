using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The registration checkbox says what the website signup says (owner decision 2026-09-23,
/// stonetavern <c>decisions/decision-2026-09-23-accounts-multiboxing-und-terms.md</c>): the player is
/// 16 or older and accepts the Terms of Service and the house rules, and the form links the Terms.
/// Before, the launcher said only "I accept the server rules", with nothing to click.
/// </summary>
public sealed class TermsCheckboxTests
{
    private static readonly string[] Languages = ["en", "de", "es", "fr", "ru"];

    [Fact]
    public void The_link_goes_to_the_terms_page_on_the_site()
    {
        Assert.Equal("https://stonetavern.app/legal/terms.html", RegisterViewModel.TermsUrl);
    }

    [Fact]
    public void The_register_form_shows_a_button_that_opens_the_terms()
    {
        var xaml = Regex.Replace(ReadSource("Views/LoginView.axaml"), "<!--.*?-->", " ", RegexOptions.Singleline);
        Assert.Contains("Command=\"{Binding Register.OpenTermsCommand}\"", xaml);
        Assert.Contains("{loc:Tr Register_ReadTerms}", xaml);
    }

    [Fact]
    public void Every_language_names_the_age_and_has_the_link_text()
    {
        foreach (var lang in Languages)
        {
            using var doc = JsonDocument.Parse(ReadSource($"Localization/lang/{lang}.json"));
            var root = doc.RootElement;
            Assert.Contains("16", root.GetProperty("Register_AcceptRules").GetString());
            Assert.Contains("16", root.GetProperty("Register_Error_Rules").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("Register_ReadTerms").GetString()), lang);
        }
    }

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "WowLauncher");
            if (File.Exists(Path.Combine(candidate, "Styles.v3.axaml")))
            {
                var path = Path.Combine(candidate, relative);
                Assert.True(File.Exists(path), $"expected source file missing: {path}");
                return File.ReadAllText(path);
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"WowLauncher source tree not found above {AppContext.BaseDirectory}");
    }
}
