using System;
using Avalonia.Markup.Xaml;

namespace WowLauncher.Localization;

/// <summary>
/// AXAML shorthand for a catalog string: <c>Text="{loc:Tr Play_News}"</c>.
///
/// <para>Resolved once at load time and handed to the target as a plain string. That is enough
/// because the launcher ships one language and never switches at runtime (see <see cref="Loc"/>).
/// If a second language is ever added, this returns a binding on a <c>Loc</c> indexer instead so the
/// tree re-renders on a switch.</para>
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    /// <summary>Catalog key, e.g. <c>Play_Cta_Play</c>. Unknown keys render as the key itself.</summary>
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
