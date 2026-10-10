using Kright.Services;
using Xunit;

namespace Kright.Tests;

// Pure logic: the auto-fix decision on top of the bundled dictionaries + bigram models.
public class WordDeciderTests
{
    private static readonly AutoFixOptions Opts =
        new(new HashSet<string>(), new HashSet<string>(), MinLettersUnknown: 5, MinConfidence: 0.85);

    private static FixDecision D(string typed, string alt, string from, string to, AutoFixOptions? o = null)
        => WordDecider.Decide(typed, alt, from, to, o ?? Opts, WordRanks.Embedded);

    [Theory]
    [InlineData("ghbdsn", "привіт")]
    [InlineData("Ghbdsn", "Привіт")]
    [InlineData("ghbdsn?", "привіт,")]      // trailing punctuation stays punctuation
    [InlineData("yt", "не")]                // 2-letter: rare EN token vs top UA word
    [InlineData("nt[yjkjubz", "технологія")]
    [InlineData("ve,m-zrbq", "будь-який")]
    [InlineData("v`zcj", "м'ясо")]
    [InlineData("ckjdj", "слово")]
    [InlineData("ntcne.", "тестую")]        // rare inflection, '.' key = ю
    [InlineData("nfr", "так")]              // "nfr" is a rare English token, "так" is top-20
    [InlineData("dsl", "від")]
    public void Fixes_Ukrainian_TypedOnEnglish(string typed, string alt)
        => Assert.True(D(typed, alt, "en", "uk").Convert);

    [Theory]
    [InlineData("руддщ", "hello")]
    [InlineData("рщц", "how")]
    public void Fixes_English_TypedOnUkrainian(string typed, string alt)
        => Assert.True(D(typed, alt, "uk", "en").Convert);

    [Theory]
    [InlineData("руку", "here", "uk", "en")]   // both real words: never touch a 3+ letter word
    [InlineData("ok", "щл", "en", "uk")]       // real word as typed
    [InlineData("SEO", "ЫУЩ", "en", "uk")]     // abbreviation
    [InlineData("iPhone", "шЗрщту", "en", "uk")] // camelCase brand
    [InlineData("hello", "руддщ", "en", "uk")]
    [InlineData("привіт", "ghbdsn", "uk", "en")]
    public void Keeps_IntendedText(string typed, string alt, string from, string to)
        => Assert.False(D(typed, alt, from, to).Convert);

    [Fact]
    public void NeverList_Wins()
    {
        var o = Opts with { Never = new HashSet<string> { "ghbdsn" } };
        Assert.False(D("ghbdsn", "привіт", "en", "uk", o).Convert);
    }

    [Fact]
    public void AlwaysList_Wins()
    {
        var o = Opts with { Always = new HashSet<string> { "інвеб" } };
        Assert.True(D("sydt,", "інвеб", "en", "uk", o).Convert);
    }

    [Fact]
    public void PreviousShortWord_FixedAlongWithNeighbour()
    {
        Assert.True(WordDecider.ShouldFixPrevious("z", "я", "en", "uk", Opts, WordRanks.Embedded));
        Assert.False(WordDecider.ShouldFixPrevious("a", "ф", "en", "uk", Opts, WordRanks.Embedded));
    }
}

public class SentenceRulesTests
{
    [Theory]
    [InlineData("Привіт.", '.', true)]
    [InlineData("Привіт!", '!', true)]
    [InlineData("так?", '?', true)]
    [InlineData(null, '.', true)]          // "2025." — digits, word unknown
    [InlineData("грн.", '.', false)]        // abbreviation
    [InlineData("т.д.", '.', false)]        // internal dot
    [InlineData("e.g.", '.', false)]
    [InlineData("Ну...", '.', false)]       // trailing off
    [InlineData("привіт", 't', false)]      // no punctuation
    [InlineData("привіт,", ',', false)]
    public void EndsSentence(string? word, char last, bool expected)
        => Assert.Equal(expected, SentenceRules.EndsSentence(word, last));
}


public class UpdateTagTests
{
    [Theory]
    [InlineData("v1.4.0", "1.4.0")]
    [InlineData("1.4", "1.4.0")]
    [InlineData("v1.4.1-beta", "1.4.1")]
    [InlineData("v.1.5.1", "1.5.1")]        // a real tag that once broke the check
    public void ParsesReleaseTags(string tag, string expected)
    {
        Assert.True(UpdateService.TryParseTag(tag, out var v));
        Assert.Equal(System.Version.Parse(expected), v);
    }
}

public class SpellRulesTests
{
    private static readonly HashSet<string> None = new();
    private static SpellVerdict Suggest(params string[] s) => new(true, SpellService.ActionSuggest, null, s);

    [Theory]
    [InlineData("пирвіт", "привіт")]   // swapped letters
    [InlineData("Пирвіт", "Привіт")]   // keeps the capital
    [InlineData("wrod", "word")]       // "helo" is in our EN list, so it's left alone by design
    public void FixesOneKeystrokeSlips(string word, string fix)
    {
        string lang = char.IsAscii(word[0]) ? "en" : "uk";
        Assert.Equal(fix, SpellRules.PickCorrection(word, Suggest(fix.ToLowerInvariant()), lang, WordRanks.Embedded, None));
    }

    [Fact]
    public void LeavesFarSuggestions() =>
        Assert.Null(SpellRules.PickCorrection("првт", Suggest("привіт"), "uk", WordRanks.Embedded, None));

    [Fact]
    public void LeavesWordsOurDictionaryKnows() =>
        Assert.Null(SpellRules.PickCorrection("тестую", Suggest("тестуй"), "uk", WordRanks.Embedded, None));

    [Fact]
    public void LeavesAbbreviations() =>
        Assert.Null(SpellRules.PickCorrection("SEOO", Suggest("SEO"), "en", WordRanks.Embedded, None));

    [Fact]
    public void Distance_CountsSwapAsOne() => Assert.Equal(1, SpellRules.Distance("пирвіт", "привіт"));
}

