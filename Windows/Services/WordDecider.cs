namespace Kright.Services;

/// <summary>Knobs for the auto-fix decision. Word sets hold lower-cased words.</summary>
public sealed record AutoFixOptions(
    IReadOnlySet<string> Never,
    IReadOnlySet<string> Always,
    int MinLettersUnknown,
    double MinConfidence,
    int RankRatio = 20,
    int RareTypedRank = 15000,
    double JunkMinConfidence = 0.5);

public readonly record struct FixDecision(bool Convert, string Reason, double Confidence)
{
    public static FixDecision No(string reason, double conf = 0) => new(false, reason, conf);
    public static FixDecision Yes(string reason, double conf) => new(true, reason, conf);
}

/// <summary>
/// Pure decision logic for auto-fix: given the word as it appeared on screen
/// (<c>typed</c>, current layout) and the same keystrokes rendered in the other
/// layout (<c>alt</c>), decide whether to replace it. No Win32 here, so it is
/// unit-testable anywhere.
///
/// Hybrid: frequency dictionaries decide whenever either side is a known word;
/// the bigram model only handles words neither dictionary knows, and with a
/// stricter bar. Bias is conservative: a missed fix costs one hotkey press, a
/// wrong fix corrupts text the user meant to type.
/// </summary>
public static class WordDecider
{
    public static FixDecision Decide(string typed, string alt, string fromLang, string toLang,
                                     AutoFixOptions o, IWordRanks ranks)
    {
        if (typed.Length == 0 || typed.Length != alt.Length) return FixDecision.No("length");

        // Trailing punctuation of the TARGET text stays as punctuation
        // ("ghbdsn?" → "привіт,"), so detection runs on the core only.
        int end = alt.Length;
        while (end > 0 && !char.IsLetter(alt[end - 1])) end--;
        if (end == 0) return FixDecision.No("no-letters");
        string aCore = alt[..end];
        string tCore = typed[..end];

        // The corrected core must look like a word: letters, plus inner
        // apostrophes / hyphens (м'ясо, будь-який, don't).
        if (!char.IsLetter(aCore[0])) return FixDecision.No("alt-leading");
        foreach (var c in aCore)
            if (!char.IsLetter(c) && !IsInnerMark(c))
                return FixDecision.No("alt-not-word");

        string tKey = Normalize(tCore);
        string aKey = Normalize(aCore);
        if (o.Never.Contains(tKey) || o.Never.Contains(Normalize(typed)))
            return FixDecision.No("never-list");
        if (o.Always.Contains(aKey)) return FixDecision.Yes("always-list", 1);

        int letters = aCore.Count(char.IsLetter);
        int typedLetters = typed.Count(char.IsLetter);
        if (letters < 2) return FixDecision.No("short");

        // Case shape of what was typed: ALLCAPS = abbreviation (SEO, API, ПДВ),
        // capitals after the first letter = brand/camelCase (iPhone, YouTube).
        var tl = tCore.Where(char.IsLetter).ToList();
        if (tl.Count >= 2 && tl.All(char.IsUpper)) return FixDecision.No("abbreviation");
        if (tl.Skip(1).Any(char.IsUpper)) return FixDecision.No("mixed-case");

        // Look the typed side up as typed AND as its core: "фсб" must count as a
        // known word even though its conversion ends in punctuation ("ac,").
        int rs = Math.Max(ranks.Rank(fromLang, Normalize(typed)), ranks.Rank(fromLang, tKey));
        int rd = ranks.Rank(toLang, aKey);

        // Short dictionary entries are mostly abbreviations and noise ("pk", "hdr"),
        // so a short target only counts if it is a genuinely common word.
        int cap = letters switch { 2 => 2000, 3 => 10000, _ => int.MaxValue };
        if (rd > cap) rd = 0;

        if (rd > 0)
        {
            if (rs == 0)
            {
                // Typed is unknown to the dictionary but reads like a real word of
                // its own layout (a rarer inflection: "купі", "філі") while the
                // target is only a so-so word ("regs", "asks") → leave it alone.
                double self = GibberishDetector.WordConfidence(tCore, fromLang) ?? 0;
                if (rd > 3000 && self >= 0.9 && !tCore.Any(c => !char.IsLetter(c) && !IsInnerMark(c)))
                    return FixDecision.No("typed-looks-native", self);
                return FixDecision.Yes("dict", 1);
            }
            // Both sides are words. Only 2-letter tokens get the frequency
            // tie-break ("yt" is a rare English token, "не" is the #2 Ukrainian
            // word); a real 3+ letter word as typed is never touched (руку ≠ here).
            if (letters == 2 && typedLetters == 2 && (long)rs > (long)o.RankRatio * rd)
                return FixDecision.Yes("dict-more-common", 0.9);
            // Longer: only when what was typed is itself a rare dictionary entry
            // ("dsl" → "від", "nfr" → "так"), never a common word.
            if (rs > o.RareTypedRank && (long)rs > (long)o.RankRatio * rd)
                return FixDecision.Yes("dict-rare-typed", 0.85);
            return FixDecision.No("both-words");
        }
        if (rs > 0) return FixDecision.No("typed-is-word");

        // Neither dictionary knows either side (rare word, inflection, slang):
        // fall back to the bigram model, longer words only.
        if (letters < o.MinLettersUnknown) return FixDecision.No("short-unknown");
        if (!GibberishDetector.Supports(fromLang) || !GibberishDetector.Supports(toLang))
            return FixDecision.No("no-model");

        // Punctuation INSIDE what was typed ("nt[yskjubz", "ve,m") never happens in
        // a real word of the source layout — strong evidence on its own, so only
        // the target side has to look like a word.
        bool typedHasJunk = tCore.Any(c => !char.IsLetter(c) && !IsInnerMark(c));
        if (typedHasJunk)
        {
            double target = GibberishDetector.WordConfidence(aCore, toLang) ?? 0;
            return target >= o.JunkMinConfidence
                ? FixDecision.Yes("junk-in-typed", target)
                : FixDecision.No("junk-but-target-weak", target);
        }

        var (wrong, conf) = GibberishDetector.LooksWrongLayout(tCore, aCore, fromLang, toLang);
        if (!wrong || conf < o.MinConfidence) return FixDecision.No("detector", conf);
        return FixDecision.Yes("detector", conf);
    }

    /// <summary>Whether a short word typed right before a fixed word, in the same
    /// wrong layout, should be fixed along with it ("z ghbdsn" → "я привіт").
    /// The fixed neighbour is the evidence, so the bar is: the conversion is a
    /// known word and what was typed is not a standalone word of its layout.</summary>
    public static bool ShouldFixPrevious(string typed, string alt, string fromLang, string toLang,
                                         AutoFixOptions o, IWordRanks ranks)
    {
        if (typed.Length == 0 || typed.Length != alt.Length || typed.Length > 3) return false;
        if (!alt.All(char.IsLetter)) return false;
        string tKey = Normalize(typed), aKey = Normalize(alt);
        if (o.Never.Contains(tKey)) return false;
        if (o.Always.Contains(aKey)) return true;
        if (ranks.Rank(toLang, aKey) == 0) return false;
        if (typed.Length == 1) return !SingleLetterWords(fromLang).Contains(tKey);
        int rs = ranks.Rank(fromLang, tKey);
        return rs == 0 || (long)rs > (long)o.RankRatio * ranks.Rank(toLang, aKey);
    }

    private static readonly HashSet<string> EnSingles = new() { "a", "i" };
    private static readonly HashSet<string> UkSingles = new() { "в", "і", "у", "з", "а", "о", "я", "й", "и" };
    private static readonly HashSet<string> NoSingles = new();

    private static HashSet<string> SingleLetterWords(string lang) => Lang2(lang) switch
    {
        "en" => EnSingles,
        "uk" => UkSingles,
        _ => NoSingles,
    };

    public static bool IsInnerMark(char c) => c is '\'' or '’' or 'ʼ' or '-';

    /// <summary>Lower-case + unify apostrophes, the form dictionaries are stored in.</summary>
    public static string Normalize(string w) =>
        w.ToLowerInvariant().Replace('’', '\'').Replace('ʼ', '\'');

    public static string Lang2(string lang)
    {
        var code = (lang ?? "").ToLowerInvariant();
        return code.Length > 2 ? code[..2] : code;
    }
}

/// <summary>Frequency rank of a word in a language (1 = most common), 0 = unknown.</summary>
public interface IWordRanks
{
    int Rank(string lang, string normalizedWord);
}

/// <summary>When the next word should start with a capital letter. Pure logic,
/// unit-tested: abbreviations ("грн.", "т. д.", "e.g.") and ellipses ("Ну...")
/// do not end a sentence.</summary>
public static class SentenceRules
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        // Ukrainian
        "т", "д", "п", "р", "рр", "ст", "с", "м", "обл", "вул", "просп", "пров", "буд", "кв",
        "грн", "коп", "тис", "млн", "млрд", "дол", "див", "напр", "зв", "ін", "под", "ім",
        "проф", "доц", "акад", "канд", "пп", "пн", "вт", "ср", "чт", "пт", "сб", "нд",
        "кг", "км", "см", "мм", "хв", "сек", "год", "шт", "уп", "др", "англ", "укр", "лат",
        // English
        "eg", "ie", "etc", "vs", "mr", "mrs", "ms", "dr", "st", "approx",
        "dept", "inc", "ltd", "co", "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep",
        "sept", "oct", "nov", "dec", "fig", "p", "pp", "vol",
    };

    /// <summary><paramref name="lastWord"/> is the word just finished, as it now reads
    /// on screen (after any layout fix), or null if unknown; <paramref name="lastChar"/>
    /// is the last character typed before the space.</summary>
    public static bool EndsSentence(string? lastWord, char lastChar)
    {
        if (lastChar is not ('.' or '!' or '?' or '…')) return false;
        if (string.IsNullOrEmpty(lastWord)) return true; // e.g. "2025." — digits, unknown word

        if (lastWord.EndsWith("..", StringComparison.Ordinal) || lastWord.EndsWith('…'))
            return false; // "Ну..." trails off, it doesn't end

        if (lastChar != '.') return true; // ! and ? always end a sentence

        // Abbreviation before the period: the letters of the last dotted chunk.
        string core = lastWord.TrimEnd('.');
        int dot = core.LastIndexOf('.');
        if (dot >= 0) return false; // "т.д." / "e.g." — internal dots mean an abbreviation
        string key = WordDecider.Normalize(core);
        return !Abbreviations.Contains(key);
    }
}

/// <summary>Which spelling corrections are safe to apply without asking.
/// Conservative on purpose: a wrong "correction" is worse than a typo left alone.</summary>
public static class SpellRules
{
    public static string? PickCorrection(string word, SpellVerdict v, string lang,
                                         IWordRanks ranks, IReadOnlySet<string> never)
    {
        if (!v.Misspelled) return null;
        if (word.Length < 3 || !char.IsLetter(word[0])) return null;
        foreach (var c in word)
            if (!char.IsLetter(c) && !WordDecider.IsInnerMark(c)) return null;

        string key = WordDecider.Normalize(word);
        if (never.Contains(key)) return null;

        // Same case-shape guards as the layout fix: ABBR, iPhone.
        var letters = word.Where(char.IsLetter).ToList();
        if (letters.Count >= 2 && letters.All(char.IsUpper)) return null;
        if (letters.Skip(1).Any(char.IsUpper)) return null;

        // Our frequency list knows it (a name, a rare form, slang Windows lacks): leave it.
        if (ranks.Rank(lang, key) > 0) return null;

        string? cand = v.Action switch
        {
            SpellService.ActionReplace => v.Replacement,
            SpellService.ActionSuggest when v.Suggestions.Count > 0 => v.Suggestions[0],
            _ => null,
        };
        if (string.IsNullOrEmpty(cand) || cand.Contains(' ')) return null;

        int dist = Distance(key, WordDecider.Normalize(cand));
        if (v.Action == SpellService.ActionReplace)
        {
            if (dist > 2) return null;   // Windows' own autocorrect pairs ("teh" → "the")
        }
        else
        {
            // A suggestion is only taken when it is one keystroke away AND a real,
            // known word — a slip, not a different word.
            if (dist != 1) return null;
            if (ranks.Rank(lang, WordDecider.Normalize(cand)) == 0) return null;
        }
        return MatchCase(word, cand);
    }

    /// <summary>Damerau–Levenshtein (optimal string alignment): a swap of two
    /// neighbouring letters counts as one edit ("пирвіт" → "привіт").</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    private static string MatchCase(string original, string cand)
    {
        if (original.Length > 0 && char.IsUpper(original[0]) && cand.Length > 0)
            return char.ToUpperInvariant(cand[0]) + cand[1..];
        return cand;
    }
}
