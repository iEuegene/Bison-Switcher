using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Kright.Services;

// ---- Windows Spell Checking API (spellcheck.h, Windows 8+) -----------------
// Interfaces declared by hand from the SDK IDL; vtable order matters.

[ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellCheckerFactory
{
    [PreserveSig] int get_SupportedLanguages(out IEnumString val);
    [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int val);
    [PreserveSig] int CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker val);
}

[ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellChecker
{
    [PreserveSig] int get_LanguageTag([MarshalAs(UnmanagedType.LPWStr)] out string val);
    [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError val);
    [PreserveSig] int Suggest([MarshalAs(UnmanagedType.LPWStr)] string word, out IEnumString val);
    // Remaining methods (Add, Ignore, AutoCorrect, …) are not called; the vtable
    // only needs to be correct up to the last method used.
}

[ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumSpellingError
{
    [PreserveSig] int Next(out ISpellingError? val);   // S_FALSE (1) when done
}

[ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellingError
{
    [PreserveSig] int get_StartIndex(out uint val);
    [PreserveSig] int get_Length(out uint val);
    [PreserveSig] int get_CorrectiveAction(out int val);
    [PreserveSig] int get_Replacement([MarshalAs(UnmanagedType.LPWStr)] out string val);
}

/// <summary>What Windows says about one word.</summary>
public sealed record SpellVerdict(bool Misspelled, int Action, string? Replacement, IReadOnlyList<string> Suggestions);

/// <summary>
/// Thin wrapper over the Windows spell checker. One instance per thread that
/// uses it (the auto-fix hook thread has its own). Spell checkers are created
/// per language on first use and cached; languages whose dictionary isn't
/// installed in Windows report <see cref="IsAvailable"/> = false.
/// </summary>
public sealed class SpellService
{
    public const int ActionNone = 0, ActionSuggest = 1, ActionReplace = 2, ActionDelete = 3;

    private static readonly Guid FactoryClsid = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
    private ISpellCheckerFactory? _factory;
    private readonly Dictionary<string, ISpellChecker?> _checkers = new();

    /// <summary>"uk" → "uk-UA", "en" → "en-US".</summary>
    public static string? Tag(string lang2) => WordDecider.Lang2(lang2) switch
    {
        "uk" => "uk-UA",
        "en" => "en-US",
        _ => null,
    };

    private ISpellCheckerFactory? Factory()
    {
        if (_factory != null) return _factory;
        try
        {
            var type = Type.GetTypeFromCLSID(FactoryClsid, throwOnError: false);
            if (type is null) return null;
            _factory = (ISpellCheckerFactory?)Activator.CreateInstance(type);
        }
        catch { _factory = null; }
        return _factory;
    }

    private ISpellChecker? Checker(string lang2)
    {
        if (_checkers.TryGetValue(lang2, out var c)) return c;
        c = null;
        try
        {
            var f = Factory();
            string? tag = Tag(lang2);
            if (f != null && tag != null && f.IsSupported(tag, out int ok) == 0 && ok != 0 &&
                f.CreateSpellChecker(tag, out var created) == 0)
                c = created;
        }
        catch { c = null; }
        _checkers[lang2] = c;
        return c;
    }

    /// <summary>Whether Windows has a spell-check dictionary for this language.</summary>
    public bool IsAvailable(string lang2) => Checker(lang2) != null;

    /// <summary>Load dictionaries now (first use costs ~100 ms).</summary>
    public void Warm(params string[] langs)
    {
        foreach (var l in langs) Check("test", l);
    }

    /// <summary>Checks a single word. Null when no dictionary is available.</summary>
    public SpellVerdict? Check(string word, string lang2)
    {
        var checker = Checker(WordDecider.Lang2(lang2));
        if (checker is null) return null;
        try
        {
            if (checker.Check(word, out var errors) != 0 || errors is null)
                return new SpellVerdict(false, ActionNone, null, Array.Empty<string>());
            if (errors.Next(out var err) != 0 || err is null)
                return new SpellVerdict(false, ActionNone, null, Array.Empty<string>());

            err.get_CorrectiveAction(out int action);
            string? replacement = null;
            if (action == ActionReplace && err.get_Replacement(out var r) == 0) replacement = r;

            var suggestions = new List<string>();
            if (action == ActionSuggest && checker.Suggest(word, out var e) == 0 && e != null)
            {
                var buf = new string[1];
                while (suggestions.Count < 5 && e.Next(1, buf, IntPtr.Zero) == 0)
                    suggestions.Add(buf[0]);
            }
            return new SpellVerdict(true, action, replacement, suggestions);
        }
        catch { return null; }
    }
}
