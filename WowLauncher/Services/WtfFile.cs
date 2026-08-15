namespace WowLauncher.Services;

/// <summary>
/// The one place that edits a <c>.wtf</c> config file (<c>WTF/Config.wtf</c>).
///
/// <para>Extracted because two callers now write into the same file for different reasons — the realm
/// binding writes <c>realmList</c>, the language switch writes <c>locale</c>/<c>textLocale</c>/
/// <c>audioLocale</c> — and two copies of "replace the line, keep the rest" is exactly the kind of
/// duplication that drifts until one of them starts appending a second <c>SET locale</c> line the
/// client silently ignores.</para>
/// </summary>
internal static class WtfFile
{
    /// <summary>Set or replace <c>SET key "value"</c>, preserving every other line and its order.
    /// A key that is not there yet is appended.
    ///
    /// <para><b>ALL occurrences are handled, not just the first (Codex review 2026-08-09).</b> The
    /// first match is rewritten in place — keeping the file's order — and every FURTHER line for the
    /// same key is removed. Rewriting only the first match is what
    /// <see cref="Platform.WtfConfigWriter"/> used to do too, and it left a file that carried the new
    /// value at the top and a stale one further down. Which of the two a 1.12.1 client honours is not
    /// documented anywhere we can rely on, so the file must not contain the question: after this call
    /// there is exactly one line for <paramref name="key"/>.</para></summary>
    public static void SetVar(string path, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var newLine = $"SET {key} \"{value}\"";
        var idx = lines.FindIndex(l => IsSettingFor(l, key));
        if (idx >= 0)
        {
            lines[idx] = newLine;
            // Walk backwards so the removals do not shift the indices still to be examined.
            for (var i = lines.Count - 1; i > idx; i--)
                if (IsSettingFor(lines[i], key)) lines.RemoveAt(i);
        }
        else
        {
            lines.Add(newLine);
        }
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    /// <summary>Every value the file carries for <paramref name="key"/>, in file order. Empty when the
    /// key is absent. Callers that must PROVE a write landed use this instead of
    /// <see cref="ReadVar"/> when they want to name the offending line in an error.</summary>
    public static IReadOnlyList<string> ReadValues(string path, string key)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        var values = new List<string>();
        foreach (var line in File.ReadAllLines(path))
            if (TrySplit(line, key, out var value))
                values.Add(value);
        return values;
    }

    /// <summary>Read <c>SET key "value"</c> back, or null when the key is absent OR ambiguous. Used to
    /// prove a write landed rather than assuming it did.
    ///
    /// <para>Ambiguity is a refusal, not a coin toss: if the file carries several lines for the key
    /// that do not all say the same thing, this returns null instead of the first one. Taking the
    /// first would report success for a file whose LAST line may be the one the client obeys — the
    /// silent wrong-realm start this readback exists to prevent.</para></summary>
    public static string? ReadVar(string path, string key)
    {
        var values = ReadValues(path, key);
        if (values.Count == 0) return null;
        foreach (var value in values)
            if (!string.Equals(value, values[0], StringComparison.OrdinalIgnoreCase))
                return null;
        return values[0];
    }

    /// <summary>Is this a <c>SET key …</c> line? Same rule for the writer and every reader, so a line
    /// one of them sees is never invisible to the other.</summary>
    public static bool IsSettingFor(string line, string key) => TrySplit(line, key, out _);

    /// <summary>Split a <c>SET key "value"</c> line, tolerating ANY whitespace after <c>SET</c> and
    /// after the key (<c>SET\trealmList "…"</c> is a line the client's own parser may well accept, and
    /// a line we do not recognise is one we neither rewrite nor catch — the exact hole closed for
    /// <c>portal</c> in the 2026-07-27 Codex round, see <see cref="Platform.WtfConfigWriter"/>).</summary>
    private static bool TrySplit(string line, string key, out string value)
    {
        value = "";
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("SET", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.Length <= 3 || !char.IsWhiteSpace(trimmed[3])) return false;
        var rest = trimmed[3..].TrimStart();
        if (!rest.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return false;
        if (rest.Length <= key.Length || !char.IsWhiteSpace(rest[key.Length])) return false;
        var raw = rest[key.Length..].Trim();
        value = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
        return true;
    }
}
