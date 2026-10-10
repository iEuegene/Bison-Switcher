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
