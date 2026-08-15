using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace WowLauncher.Localization;

/// <summary>
/// AXAML shorthand for a catalog string: <c>Text="{loc:Tr Play_News}"</c>.
///
/// <para>Returns a BINDING against the live catalog rather than the string itself. Resolving once at
/// load time was enough while the launcher shipped one language; now that it follows the player's
/// language pick, a copy taken at load time would leave every already-rendered label in the old
/// language until the window was rebuilt — and rebuilding the tree loses scroll position, selection
/// and focus for what is meant to be a setting.</para>
///
/// <para>The binding is one-way against an indexer, so a single notification on
/// <see cref="Loc.CatalogView"/> re-resolves every visible string at once.</para>
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    /// <summary>Catalog key, e.g. <c>Play_Cta_Play</c>. Unknown keys fall back to English and, failing
    /// that, render as the key itself.</summary>
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = Loc.Catalog,
            Mode = BindingMode.OneWay,
        };
}
