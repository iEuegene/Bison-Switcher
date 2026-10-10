using System.IO;
using System.Reflection;

namespace Kright.Services;

/// <summary>
/// Frequency dictionaries (one word per line, most common first) used by
/// <see cref="WordDecider"/>. Bundled as embedded resources <c>Dict/words_&lt;lang&gt;.txt</c>;
/// languages without a list simply return rank 0 and fall back to the bigram model.
/// Source: wordfreq lists (CC BY-SA 4.0), filtered to plain words.
/// </summary>
public sealed class WordRanks : IWordRanks
{
    private readonly Func<string, IEnumerable<string>?> _loader;
    private readonly Dictionary<string, Dictionary<string, int>> _byLang = new();
    private readonly object _lock = new();

    public WordRanks(Func<string, IEnumerable<string>?> loader) => _loader = loader;

    /// <summary>Lists embedded in the Kright assembly.</summary>
    public static WordRanks Embedded { get; } = new(LoadEmbedded);

    public int Rank(string lang, string normalizedWord)
    {
        var table = Table(WordDecider.Lang2(lang));
        return table.TryGetValue(normalizedWord, out var r) ? r : 0;
    }

    /// <summary>How many words the list for <paramref name="lang"/> holds (0 = none bundled).</summary>
    public int Count(string lang) => Table(WordDecider.Lang2(lang)).Count;

    /// <summary>Load every bundled list now, so the first keystroke-time lookup
    /// doesn't pay for parsing inside the keyboard hook.</summary>
    public void Warm(params string[] langs)
    {
        foreach (var l in langs) Table(WordDecider.Lang2(l));
    }

    private Dictionary<string, int> Table(string lang)
    {
        lock (_lock)
        {
            if (_byLang.TryGetValue(lang, out var t)) return t;
            t = new Dictionary<string, int>(StringComparer.Ordinal);
            var lines = _loader(lang);
            if (lines != null)
            {
                int rank = 0;
                foreach (var raw in lines)
                {
                    var w = raw.Trim();
                    if (w.Length == 0) continue;
                    rank++;
                    t.TryAdd(WordDecider.Normalize(w), rank);
                }
            }
            _byLang[lang] = t;
            return t;
        }
    }

    private static IEnumerable<string>? LoadEmbedded(string lang)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($"words_{lang}.txt", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd().Split('\n');
    }
}
