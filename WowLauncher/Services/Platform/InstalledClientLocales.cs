namespace WowLauncher.Services.Platform;

/// <summary>
/// Which languages a modern (1.14.2, CASC) client installation can actually be started in.
///
/// <para><b>Why this has to be read and not assumed (measured 2026-08-03).</b> A locale the client
/// does not carry is not a graceful fallback to English — it is a hard stop before any window
/// appears: <c>ERROR #134 (0x85100086) Fatal condition! Unable to open FileData ID …: Can't find
/// file in build manifest</c>. Reproduced by setting <c>textLocale</c> to <c>itIT</c>, which this
/// build does not carry. So offering a language we have not verified means handing a player a
/// client that dies on launch, with a Blizzard error box that says nothing about languages.</para>
///
/// <para><b>Where the truth lives.</b> <c>&lt;install&gt;/.build.info</c>, the file the Blizzard
/// installer writes beside the <c>Data</c> folder, carries a <c>Tags</c> column whose entries look
/// like <c>Windows x86_64 EU? acct-BGR? geoip-BG? deDE text?</c> — one per locale, separated by
/// <c>:</c>, with <c>text</c> and <c>speech</c> as separate entries. Verified against reality on the
/// local 1.14.2 install: every locale tagged <c>text</c> started the client in that language, and a
/// locale absent from the list (itIT) produced the crash above.</para>
///
/// <para><b>Fail closed.</b> No file, no readable tags, no parse — the answer is English only. A
/// launcher that guesses here trades a missing menu entry for a client that will not start.</para>
/// </summary>
public static class InstalledClientLocales
{
    /// <summary>The one locale every install of this client carries, and the answer whenever the
    /// installation cannot be read.</summary>
    public const string Fallback = "enUS";

    /// <summary>What one installation offers: which languages its text is available in, and which of
    /// those also carry spoken audio (a locale with text but no speech plays English voice-over —
    /// setting <c>audioLocale</c> to it would be the same #134 crash).</summary>
    /// <param name="Text">Locale codes usable as <c>textLocale</c>, always including <see cref="Fallback"/>.</param>
    /// <param name="Speech">Locale codes usable as <c>audioLocale</c>.</param>
    public sealed record Available(IReadOnlyList<string> Text, IReadOnlyList<string> Speech);

    /// <summary>Read the locales of the installation containing <paramref name="clientDir"/> (the
    /// directory holding <c>WowClassic.exe</c>). <c>.build.info</c> sits two levels up, beside
    /// <c>Data</c>: <c>&lt;root&gt;/World of Warcraft/.build.info</c> for
    /// <c>&lt;root&gt;/World of Warcraft/_classic_era_/WowClassic.exe</c>.</summary>
    public static Available ForClientDir(string clientDir)
    {
        try
        {
            var installRoot = Path.GetDirectoryName(Path.GetFullPath(clientDir));
            if (installRoot is null) return EnglishOnly;
            return Read(Path.Combine(installRoot, ".build.info"));
        }
        catch (Exception)
        {
            return EnglishOnly;
        }
    }

    /// <summary>Read one <c>.build.info</c>. Never throws: any unreadable or unexpected file means
    /// English only.</summary>
    public static Available Read(string buildInfoPath)
    {
        try
        {
            if (!File.Exists(buildInfoPath)) return EnglishOnly;
            return Parse(File.ReadAllText(buildInfoPath));
        }
        catch (Exception)
        {
            return EnglishOnly;
        }
    }

    /// <summary>The pure core, so the format is provable without a 7 GB client on disk.</summary>
    internal static Available Parse(string buildInfo)
    {
        var lines = buildInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return EnglishOnly;

        // Header cells are "Name!TYPE:LEN" — the name is what matters.
        var header = lines[0].Split('|').Select(h => h.Split('!')[0].Trim()).ToList();
        var tagsColumn = header.FindIndex(h => h.Equals("Tags", StringComparison.OrdinalIgnoreCase));
        var activeColumn = header.FindIndex(h => h.Equals("Active", StringComparison.OrdinalIgnoreCase));
        if (tagsColumn < 0) return EnglishOnly;

        var text = new SortedSet<string>(StringComparer.Ordinal) { Fallback };
        var speech = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split('|');
            if (cells.Length <= tagsColumn) continue;
            // Several branches can be listed; only an active one describes what is installed here.
            if (activeColumn >= 0 && activeColumn < cells.Length && cells[activeColumn].Trim() != "1") continue;

            foreach (var entry in cells[tagsColumn].Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var words = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // "… deDE text?" — the locale is the word before the kind, both may carry a trailing '?'.
                for (var i = 1; i < words.Length; i++)
                {
                    var kind = words[i].TrimEnd('?');
                    if (kind != "text" && kind != "speech") continue;
                    var locale = words[i - 1].TrimEnd('?');
                    if (!LooksLikeLocale(locale)) continue;
                    if (kind == "text") text.Add(locale); else speech.Add(locale);
                }
            }
        }

        return new Available(text.ToList(), speech.ToList());
    }

    /// <summary>WoW locale codes are exactly two lowercase letters followed by two uppercase ones
    /// (deDE, zhTW). Checking the shape keeps stray tag words out of a language menu.</summary>
    internal static bool LooksLikeLocale(string s) =>
        s.Length == 4
        && char.IsAsciiLetterLower(s[0]) && char.IsAsciiLetterLower(s[1])
        && char.IsAsciiLetterUpper(s[2]) && char.IsAsciiLetterUpper(s[3]);

    private static Available EnglishOnly => new([Fallback], []);
}
