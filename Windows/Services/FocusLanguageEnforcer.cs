using System.Windows.Threading;

namespace Kright.Services;

/// <summary>Watches the focused field and switches the keyboard to English when
/// it lands on an email / URL / password / payment field. Opt-in. Runs on a
/// light DispatcherTimer; acts only on focus change.</summary>
public sealed class FocusLanguageEnforcer
{
    private static readonly HashSet<string> LatinKinds = new() { "Email", "URL", "Password", "Payment" };
    private readonly DispatcherTimer _timer;
    private string _lastSignature = "";

    public bool Enabled
    {
        get => AppSettings.Current.AutoEnglishOnLatinFields;
        set
        {
            AppSettings.Current.AutoEnglishOnLatinFields = value;
            AppSettings.Save();
            _timer.Start(); // always on: auto-fix reads LastGuess
        }
    }

    public bool AddressBarEnabled
    {
        get => AppSettings.Current.AutoEnglishInAddressBar;
        set
        {
            AppSettings.Current.AutoEnglishInAddressBar = value;
            AppSettings.Save();
        }
    }

    public string? LastAction { get; private set; }

    /// <summary>Kind of the focused field as of the last tick (see FocusedField.Guess).</summary>
    public string? LastGuess { get; private set; }

    public FocusLanguageEnforcer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Tick();
    }

    // The timer always runs (cheap, 400 ms): auto-fix needs to know when the
    // caret is in an address bar even if auto-English is off.
    public void StartIfEnabled() => _timer.Start();

    private void Tick()
    {
        var field = FocusInspector.Focused();
        if (field is null) return;

        string sig = $"{field.ControlType}|{field.Name}|{field.IsPassword}|{field.ClassName}|{field.AutomationId}";
        if (sig == _lastSignature) return;
        _lastSignature = sig;

        // A new field means a new caret position for auto-fix.
        App.AutoFix?.FocusMoved();

        string kind = field.Guess;
        LastGuess = kind;
        bool wanted = kind == FocusedField.AddressBarKind
            ? AddressBarEnabled
            : Enabled && LatinKinds.Contains(kind);
        if (!wanted) return;

        // Already on a Latin layout (English, French, Spanish…)? Leave it — only
        // a non-Latin script (Hebrew, Arabic, Cyrillic…) garbles these fields.
        var current = LanguageManager.Current();
        if (current is null || LanguageManager.IsLatin(current.Hkl)) return;
        var target = LanguageManager.FirstLatin();
        if (target is null) return;

        LanguageManager.Switch(target.Hkl);
        LastAction = $"Switched to {target.Name} for {field.Guess} field";
    }
}
