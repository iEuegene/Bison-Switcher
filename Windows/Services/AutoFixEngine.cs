using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Kright.Native;

namespace Kright.Services;

/// <summary>
/// Auto-fix: watches keystrokes with a low-level keyboard hook, and when a word
/// ends (Space / Tab / Enter) decides — via <see cref="WordDecider"/> — whether it
/// was typed in the wrong layout. If so, the boundary key is swallowed and one
/// atomic SendInput batch deletes the word, types the corrected one, then the
/// boundary key; the keyboard switches to the corrected language.
///
/// The buffer holds virtual keys + Shift/Caps, never characters, so the same
/// keystrokes can be rendered in either layout. Nothing is logged or stored:
/// the buffer is the current word only and is cleared at every boundary.
///
/// The hooks live on their own thread with its own message loop. The UI thread
/// polls UI Automation every few hundred ms and UIA can block on a hung app; a
/// hook sharing that thread would stall typing and get silently unhooked by
/// Windows (LowLevelHooksTimeout). Shared state is guarded by <c>_gate</c>; word
/// lists arrive as immutable snapshots built on the UI thread.
/// </summary>
public sealed class AutoFixEngine : IDisposable
{
    private readonly record struct Stroke(uint Vk, bool Shift, bool Caps);

    private sealed record PrevWord(List<Stroke> Keys, IntPtr Hkl);

    /// <summary>What the last fix did, so an immediate Backspace can undo it.</summary>
    private sealed record LastFix(string Removed, string Inserted, IntPtr FromHkl, string LearnKey);

    private sealed record Lists(HashSet<string> Never, HashSet<string> Always, HashSet<string> Excluded);

    private readonly NativeMethods.LowLevelKeyboardProc _kbProc;
    private readonly NativeMethods.LowLevelKeyboardProc _mouseProc; // same signature
    private IntPtr _kbHook = IntPtr.Zero, _mouseHook = IntPtr.Zero;
    private Thread? _thread;
    private uint _threadId;

    private readonly object _gate = new();
    private readonly List<Stroke> _word = new();
    private bool _dirty;        // word didn't start cleanly (mid-text edit, digits…)
    private bool _shift;
    private bool _caps, _capsDown;
    private PrevWord? _prev;    // previous short word, for "z ghbdsn" → "я привіт"
    private char _lastChar;     // last printable character typed (current layout)
    private bool _capPending;   // next letter starts a sentence → make it upper-case
    private string? _lastInserted; // text the last fix typed, if any
    private SpellService? _spell;  // created and used on the hook thread only
    private int _spellSlow;        // checks over budget; 3 of them suspend spelling for the session
    private const uint WM_APP_WARM_SPELL = 0x8001;
    private LastFix? _last;

    private static readonly uint OwnPid = (uint)Environment.ProcessId;
    private readonly Dictionary<IntPtr, string> _langCache = new();
    private readonly System.Windows.Threading.Dispatcher _ui;
    private volatile Lists _lists;

    /// <summary>Last decision, for the Detect tab (word → result · reason).</summary>
    public string? LastDecision { get; private set; }

    /// <summary>Structured form of the last decision, for the Diagnostics page.</summary>
    public sealed record WordInfo(string Typed, string Alt, bool Fixed, string Reason);
    public WordInfo? LastWord { get; private set; }

    /// <summary>Counters for this run only (never persisted).</summary>
    public int FixCount => _fixCount;
    public int UndoCount => _undoCount;
    /// <summary>Last applied fix as "typed → fixed", for the overview.</summary>
    public string? LastFixText { get; private set; }
    private int _fixCount, _undoCount;

    /// <summary>Raised on the UI thread.</summary>
    public event Action? StateChanged;

    public AutoFixEngine()
    {
        _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher; // created on the UI thread
        _kbProc = KeyboardProc;   // keep delegates alive while hooks are installed
        _mouseProc = MouseProc;
        _lists = BuildLists();
    }

    public bool Enabled
    {
        get => AppSettings.Current.AutoFixEnabled;
        set
        {
            AppSettings.Current.AutoFixEnabled = value;
            AppSettings.Save();
            Reset();
            RaiseStateChanged();
        }
    }

    public void Start()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            _caps = (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
            var mod = NativeMethods.GetModuleHandle(null!);
            _kbHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _kbProc, mod, 0);
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, mod, 0);
            ready.Set();

            if (AppSettings.Current.SpellCheckEnabled) WarmSpell();

            // LL hooks are delivered through this thread's message loop.
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP_WARM_SPELL) { WarmSpell(); continue; }
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            if (_kbHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_kbHook);
            if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _kbHook = _mouseHook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "Bison Switcher auto-fix hooks",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Stop()
    {
        if (_thread == null) return;
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>Spell check was just switched on: load dictionaries on the hook
    /// thread now, so the first word doesn't wait ~100 ms for them.</summary>
    public void RequestSpellWarmup()
    {
        if (_threadId != 0)
            NativeMethods.PostThreadMessage(_threadId, WM_APP_WARM_SPELL, IntPtr.Zero, IntPtr.Zero);
    }

    private void WarmSpell()
    {
        try
        {
            lock (_gate)
            {
                _spell ??= new SpellService();
                _spell.Warm("uk", "en");
                _spellSlow = 0;
            }
        }
        catch { }
    }

    /// <summary>Forget the current word (start clean).</summary>
    public void Reset()
    {
        lock (_gate) ResetLocked();
    }

    /// <summary>The caret may have moved (click, other window, other field). With
    /// nothing typed yet, start clean — clicking into a field and typing must still
    /// get fixed. Mid-word, the rest of the word would be a fragment ("ghb|dsn"),
    /// so skip it rather than "fix" half a word.</summary>
    public void FocusMoved()
    {
        lock (_gate)
        {
            if (_word.Count > 0) MarkDirtyLocked();
            else ResetLocked();
        }
    }

    /// <summary>Rebuild the word-list snapshot. Call on the UI thread after the
    /// lists or exclusions change.</summary>
    public void InvalidateLists() => _lists = BuildLists();

    private static Lists BuildLists()
    {
        var s = AppSettings.Current;
        return new Lists(
            new HashSet<string>(s.NeverConvert.Select(WordDecider.Normalize)),
            new HashSet<string>(s.AlwaysConvert.Select(WordDecider.Normalize)),
            new HashSet<string>(s.AutoFixExcludedApps.Select(a => a.ExeName), StringComparer.OrdinalIgnoreCase));
    }

    private void RaiseStateChanged() => _ui.InvokeAsync(() => StateChanged?.Invoke());

    private void ResetLocked()
    {
        _word.Clear();
        _dirty = false;
        _prev = null;
        _last = null;
        _capPending = false;
        _lastChar = '\0';
    }

    private void MarkDirtyLocked()
    {
        _word.Clear();
        _dirty = true;
        _prev = null;
        _last = null;
        _capPending = false;
    }

    // ---------------------------------------------------------------- hooks

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            // A click usually moves the caret.
            if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
                    or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN)
                FocusMoved();
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var k = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                bool swallow;
                lock (_gate) swallow = HandleKey((int)wParam, k);
                if (swallow) return (IntPtr)1;
            }
            catch
            {
                // Never let an exception escape into the hook chain.
                Reset();
            }
        }
        return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    /// <summary>Returns true to swallow the key. Called under <c>_gate</c>.</summary>
    private bool HandleKey(int msg, NativeMethods.KBDLLHOOKSTRUCT k)
    {
        // Ours (and any other tool's) synthetic input: not the user typing.
        if ((k.flags & NativeMethods.LLKHF_INJECTED) != 0) return false;

        int vk = (int)k.vkCode;
        bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;

        if (vk is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT)
        {
            _shift = down;
            return false;
        }
        // Caps Lock toggle tracked here: GetKeyState on a thread that never gets
        // keyboard focus doesn't follow the toggle reliably. Auto-repeat ignored.
        if (vk == NativeMethods.VK_CAPITAL)
        {
            if (down && !_capsDown) _caps = !_caps;
            _capsDown = down;
            return false;
        }
        if (!down) return false;

        // Modifier keys themselves change nothing in the text.
        if (vk is NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
                or NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU
                or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN)
            return false;

        if (!AppSettings.Current.AutoFixEnabled && !AppSettings.Current.AutoCapitalize) return false;

        // Password field: don't even buffer the keys.
        if (App.Privacy?.Sensitive == true)
        {
            MarkDirtyLocked();
            return false;
        }

        // Shortcuts (Ctrl+V, Alt+Tab, AltGr letters…) change text in ways we can't
        // track: drop the word and wait for the next clean boundary.
        if (msg == NativeMethods.WM_SYSKEYDOWN || ModifierHeld())
        {
            MarkDirtyLocked();
            return false;
        }

        if (vk == NativeMethods.VK_BACK)
        {
            if (_last != null && AppSettings.Current.AutoFixUndoOnBackspace)
            {
                Undo();
                return true;
            }
            _last = null;
            _capPending = false;   // deleting the capital lets you retype it lower-case
            _lastChar = '\0';
            if (_word.Count > 0) _word.RemoveAt(_word.Count - 1);
            else { _dirty = true; _prev = null; } // backspacing into older text
            return false;
        }

        _last = null; // any other key closes the undo window

        if (vk is NativeMethods.VK_SPACE or NativeMethods.VK_TAB or NativeMethods.VK_RETURN)
            return OnBoundary((ushort)vk);

        char ch = CharOf((uint)vk);

        if (IsWordKey((uint)vk))
        {
            if (_capPending && _word.Count == 0 && !_dirty && TryCapitalize((uint)vk, ch))
                return true;
            _capPending = false;
            _lastChar = ch;
            if (_word.Count < 48) _word.Add(new Stroke((uint)vk, _shift, _caps));
            else _dirty = true;
            return false;
        }

        if (vk == NativeMethods.VK_ESCAPE) { ResetLocked(); return false; }

        // Digits, arrows, Home/End, Delete, numpad, F-keys…: the word is no longer
        // a clean run of letters typed at the caret. Keep the character itself:
        // "!" and "?" live on the digit row in both layouts.
        MarkDirtyLocked();
        _lastChar = ch;
        return false;
    }

    private static bool ModifierHeld() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;

    /// <summary>The character this key types right now in the foreground layout,
    /// or '\0' for keys that type nothing printable.</summary>
    private char CharOf(uint vk)
    {
        IntPtr hkl = LanguageManager.CurrentHkl();
        var map = _shift ? KeyboardLayoutMap.ShiftedMap(hkl) : KeyboardLayoutMap.ForwardMap(hkl);
        if (!map.TryGetValue(vk, out var s) || s.Length != 1) return '\0';
        char c = s[0];
        if (_caps && char.IsLetter(c))
            c = char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
        return c;
    }

    /// <summary>First letter of a sentence typed in lower case: swallow the key and
    /// type the capital instead. The buffer records it as Shift+key, so a later
    /// layout fix renders the capital too ("Ghbdsn" → "Привіт").</summary>
    private bool TryCapitalize(uint vk, char ch)
    {
        _capPending = false;
        if (!AppSettings.Current.AutoCapitalize) return false;
        if (!char.IsLetter(ch) || !char.IsLower(ch)) return false;
        if (BlockReason() != null) return false;
        if (!KeystrokeReplacer.SendReplacement(0, char.ToUpperInvariant(ch).ToString())) return false;
        _lastChar = char.ToUpperInvariant(ch);
        _word.Add(new Stroke(vk, Shift: true, Caps: false));
        return true;
    }

    /// <summary>Why we must not type into the foreground app right now, or null.</summary>
    private string? BlockReason()
    {
        if (App.Privacy?.Sensitive == true) return "password";
        var fg = App.AppEnforcer;
        fg.RefreshCurrent();
        if (fg.CurrentPid == OwnPid) return "own";
        if (fg.CurrentExePath is { } exe && _lists.Excluded.Contains(Path.GetFileName(exe))) return "app-excluded";
        if (fg.CurrentIsElevated && !Environment.IsPrivilegedProcess) return "elevated";
        return null;
    }

    /// <summary>Letter keys plus the OEM keys, which are letters on Cyrillic
    /// layouts (х ї ж є б ю ') and punctuation on Latin ones.</summary>
    private static bool IsWordKey(uint vk) =>
        (vk >= 0x41 && vk <= 0x5A) ||
        (vk >= 0xBA && vk <= 0xC0) ||
        (vk >= 0xDB && vk <= 0xDE) ||
        vk == 0xE2;

    // ------------------------------------------------------------- the fix

    private bool OnBoundary(ushort boundaryVk)
    {
        // Extra spaces after ". " keep the capital pending.
        if (boundaryVk == NativeMethods.VK_SPACE && _capPending && _word.Count == 0 && !_dirty)
            return false;

        // What the finished word reads like on screen, for the sentence check.
        string? shown = !_dirty && _word.Count > 0 ? Render(_word, LanguageManager.CurrentHkl()) : null;

        bool fixedIt;
        _lastInserted = null;
        try { fixedIt = AppSettings.Current.AutoFixEnabled && TryFix(boundaryVk); }
        catch { fixedIt = false; }

        // Right layout, wrong letters: the Windows spell checker gets a say.
        if (!fixedIt && shown != null && AppSettings.Current.SpellCheckEnabled)
        {
            try { fixedIt = TrySpell(boundaryVk, shown); }
            catch { fixedIt = false; }
        }

        if (fixedIt && _lastInserted is { Length: > 0 } ins)
        {
            shown = ins;
            _lastChar = ins[^1];
        }
        _capPending = boundaryVk == NativeMethods.VK_SPACE
                      && AppSettings.Current.AutoCapitalize
                      && SentenceRules.EndsSentence(shown, _lastChar);
        _lastChar = '\0';

        if (!fixedIt)
        {
            // Remember a short clean word followed by Space: if the NEXT word gets
            // fixed, this one was very likely typed in the same wrong layout.
            _prev = boundaryVk == NativeMethods.VK_SPACE && !_dirty && _word.Count is > 0 and <= 3
                ? new PrevWord(new List<Stroke>(_word), LanguageManager.CurrentHkl())
                : null;
        }
        _word.Clear();
        _dirty = false;
        return fixedIt;
    }

    private bool TryFix(ushort boundaryVk)
    {
        if (_dirty || _word.Count == 0) return false;
        if (App.Privacy?.Sensitive == true) return false; // password field: hands off

        var lists = _lists;
        var fg = App.AppEnforcer;
        fg.RefreshCurrent();
        if (fg.CurrentPid == OwnPid) return false;
        if (fg.CurrentExePath is { } exe && lists.Excluded.Contains(Path.GetFileName(exe)))
        {
            LastDecision = $"{Path.GetFileName(exe)}: excluded";
            LastWord = new WordInfo(Path.GetFileName(exe), "", false, "app-excluded");
            return false;
        }

        // UIPI: Windows silently drops input injected into an admin window from a
        // normal process. Fixing would swallow the Space and type nothing.
        if (fg.CurrentIsElevated && !Environment.IsPrivilegedProcess)
        {
            string app = fg.CurrentExePath is { } p0 ? Path.GetFileName(p0) : "?";
            LastDecision = $"{app}: elevated";
            LastWord = new WordInfo(app, "", false, "elevated");
            return false;
        }

        IntPtr cur = LanguageManager.CurrentHkl();
        bool curLatin = LanguageManager.IsLatin(cur);
        var other = LanguageManager.Enabled().FirstOrDefault(l => LanguageManager.IsLatin(l.Hkl) != curLatin);
        if (other is null) return false;

        string? typed = Render(_word, cur);
        string? alt = Render(_word, other.Hkl);
        if (typed is null || alt is null) return false;

        string fromLang = LangOf(cur);
        var opts = Options(lists);
        var d = WordDecider.Decide(typed, alt, fromLang, other.Lang, opts, WordRanks.Embedded);
        LastDecision = $"{typed} → {alt}: {(d.Convert ? "FIX" : "keep")} · {d.Reason}";
        LastWord = new WordInfo(typed, alt, d.Convert, d.Reason);
        if (!d.Convert) return false;

        string removed = typed, inserted = alt;
        if (_prev is { } p && p.Hkl == cur)
        {
            string? pt = Render(p.Keys, cur), pa = Render(p.Keys, other.Hkl);
            if (pt != null && pa != null &&
                WordDecider.ShouldFixPrevious(pt, pa, fromLang, other.Lang, opts, WordRanks.Embedded))
            {
                removed = pt + " " + typed;
                inserted = pa + " " + alt;
            }
        }

        // Chromium/Firefox address bars inline-autocomplete: the suggested tail is
        // selected after the caret, and the first Backspace would only delete it.
        bool addressBar = App.Enforcer?.LastGuess == FocusedField.AddressBarKind;

        if (!KeystrokeReplacer.SendReplacement(removed.Length, inserted, boundaryVk, deleteFirst: addressBar))
            return false;

        LanguageManager.Switch(other.Hkl);
        Interlocked.Increment(ref _fixCount);
        _lastInserted = inserted;
        LastFixText = $"{removed} → {inserted}";

        _prev = null;
        _last = boundaryVk == NativeMethods.VK_SPACE
            ? new LastFix(removed, inserted, cur, WordDecider.Normalize(typed))
            : null; // after Tab/Enter the caret has left the word — no undo
        return true;
    }

    private bool TrySpell(ushort boundaryVk, string shown)
    {
        if (_spellSlow >= 3) return false;
        if (BlockReason() != null) return false;

        int end = shown.Length;
        while (end > 0 && !char.IsLetter(shown[end - 1])) end--;
        string core = shown[..end], tail = shown[end..];
        if (core.Length < 3) return false;

        IntPtr cur = LanguageManager.CurrentHkl();
        string lang = LangOf(cur);
        _spell ??= new SpellService();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var verdict = _spell.Check(core, lang);
        sw.Stop();
        if (sw.ElapsedMilliseconds > 120 && ++_spellSlow >= 3)
            LastDecision = "spell check too slow — paused until restart";
        if (verdict is null) return false;

        string? fix = SpellRules.PickCorrection(core, verdict, lang, WordRanks.Embedded, _lists.Never);
        if (fix is null || fix == core) return false;

        string inserted = fix + tail;
        bool addressBar = App.Enforcer?.LastGuess == FocusedField.AddressBarKind;
        if (!KeystrokeReplacer.SendReplacement(shown.Length, inserted, boundaryVk, deleteFirst: addressBar))
            return false;

        Interlocked.Increment(ref _fixCount);
        LastFixText = $"{shown} → {inserted}";
        LastDecision = $"{core} → {fix}: SPELL";
        LastWord = new WordInfo(core, fix, true, "spell");
        _lastInserted = inserted;
        _prev = null;
        _last = boundaryVk == NativeMethods.VK_SPACE
            ? new LastFix(shown, inserted, cur, WordDecider.Normalize(core))
            : null;
        return true;
    }

    private void Undo()
    {
        var f = _last!;
        _last = null;
        // Remove the inserted text + the space we typed, put back the original + space.
        KeystrokeReplacer.SendReplacement(f.Inserted.Length + 1, f.Removed, NativeMethods.VK_SPACE);
        LanguageManager.Switch(f.FromHkl);
        LastDecision = $"undo: {f.Inserted} → {f.Removed}";
        LastWord = new WordInfo(f.Inserted, f.Removed, false, "undo");
        Interlocked.Increment(ref _undoCount);
        LastFixText = $"скасовано: {f.Inserted} → {f.Removed}";
        _word.Clear();
        _dirty = false;
        _prev = null;

        // Settings are only ever mutated on the UI thread.
        if (AppSettings.Current.AutoFixLearnFromUndo)
            _ui.InvokeAsync(() => UserWords.AddNever(f.LearnKey));
        RaiseStateChanged();
    }

    // ------------------------------------------------------------- helpers

    private static string? Render(List<Stroke> keys, IntPtr hkl)
    {
        var plain = KeyboardLayoutMap.ForwardMap(hkl);
        var shifted = KeyboardLayoutMap.ShiftedMap(hkl);
        var chars = new char[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            var map = k.Shift ? shifted : plain;
            if (!map.TryGetValue(k.Vk, out var s) || s.Length != 1) return null;
            char c = s[0];
            if (k.Caps && char.IsLetter(c))
                c = char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
            chars[i] = c;
        }
        return new string(chars);
    }

    private string LangOf(IntPtr hkl)
    {
        if (_langCache.TryGetValue(hkl, out var l)) return l;
        try { l = new CultureInfo((int)((long)hkl & 0xFFFF)).TwoLetterISOLanguageName.ToLowerInvariant(); }
        catch { l = ""; }
        _langCache[hkl] = l;
        return l;
    }

    private static AutoFixOptions Options(Lists lists) => AppSettings.Current.AutoFixSensitivity switch
    {
        0 => new AutoFixOptions(lists.Never, lists.Always, 6, 0.92, RankRatio: int.MaxValue),
        2 => new AutoFixOptions(lists.Never, lists.Always, 4, 0.75),
        _ => new AutoFixOptions(lists.Never, lists.Always, 5, 0.85),
    };
}

/// <summary>The personal word lists, shared by auto-fix, the hotkey and Settings.</summary>
public static class UserWords
{
    public static bool AddNever(string word) => Add(AppSettings.Current.NeverConvert, word);
    public static bool AddAlways(string word) => Add(AppSettings.Current.AlwaysConvert, word);

    public static void Remove(List<string> list, string word)
    {
        list.RemoveAll(w => WordDecider.Normalize(w) == WordDecider.Normalize(word));
        AppSettings.Save();
        App.AutoFix?.InvalidateLists();
    }

    /// <summary>Hotkey fixed <paramref name="typed"/> into <paramref name="converted"/>:
    /// the typed form is clearly not a keeper, and the second time the same word
    /// needs a manual fix it goes into the always-list.</summary>
    public static void LearnFromHotkey(string typed, string converted)
    {
        var s = AppSettings.Current;
        if (!s.AutoFixLearnFromHotkey) return;
        string core = converted.Trim();
        int end = core.Length;
        while (end > 0 && !char.IsLetter(core[end - 1])) end--;
        core = core[..end];
        if (core.Length < 2 || core.Contains(' ') || !core.All(c => char.IsLetter(c) || WordDecider.IsInnerMark(c)))
            return;
        string key = WordDecider.Normalize(core);

        s.NeverConvert.RemoveAll(w => WordDecider.Normalize(w) == WordDecider.Normalize(typed.Trim()));
        int n = s.HotkeyFixCounts.TryGetValue(key, out var c0) ? c0 + 1 : 1;
        if (n >= 2)
        {
            s.HotkeyFixCounts.Remove(key);
            Add(s.AlwaysConvert, key);
        }
        else
        {
            s.HotkeyFixCounts[key] = n;
            AppSettings.Save();
        }
        App.AutoFix?.InvalidateLists();
    }

    private static bool Add(List<string> list, string word)
    {
        var w = WordDecider.Normalize(word.Trim());
        if (w.Length == 0 || list.Any(x => WordDecider.Normalize(x) == w)) return false;
        list.Add(w);
        AppSettings.Save();
        App.AutoFix?.InvalidateLists();
        return true;
    }
}
