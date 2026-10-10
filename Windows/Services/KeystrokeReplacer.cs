using System.Threading;
using Kright.Native;

namespace Kright.Services;

/// <summary>Replaces text by simulating keystrokes (SendInput), for fields that
/// reject UIA writes — consoles (cmd, PowerShell, Windows Terminal), etc. Sends
/// N backspaces then types the correction as Unicode input. Injected events are
/// tagged via dwExtraInfo so the keyboard hook can ignore Kright's own input.</summary>
public static class KeystrokeReplacer
{
    public static void ReplaceLastWord(int originalLength, string replacement)
    {
        if (originalLength <= 0) return;

        for (int i = 0; i < originalLength; i++)
        {
            SendKey(NativeMethods.VK_BACK, '\0', keyUp: false);
            SendKey(NativeMethods.VK_BACK, '\0', keyUp: true);
            Thread.Sleep(6);
        }
        Thread.Sleep(10);
        foreach (var ch in replacement)
        {
            SendKey(0, ch, keyUp: false, unicode: true);
            SendKey(0, ch, keyUp: true, unicode: true);
            Thread.Sleep(2);
        }
    }

    /// <summary>Atomic replacement for auto-fix: one SendInput call carrying
    /// [optional Delete] + N backspaces + the replacement text + an optional trailing key
    /// (the Space/Tab/Enter that triggered the fix). A single batch is not
    /// interleaved with the user's own keystrokes, unlike the paced loop above.</summary>
    public static bool SendReplacement(int backspaces, string text, ushort trailingVk = 0, bool deleteFirst = false)
    {
        var inputs = new List<NativeMethods.INPUT>(backspaces * 2 + text.Length * 2 + 4);
        if (deleteFirst)
        {
            inputs.Add(Key((ushort)NativeMethods.VK_DELETE, '\0', false));
            inputs.Add(Key((ushort)NativeMethods.VK_DELETE, '\0', true));
        }
        for (int i = 0; i < backspaces; i++)
        {
            inputs.Add(Key(NativeMethods.VK_BACK, '\0', false));
            inputs.Add(Key(NativeMethods.VK_BACK, '\0', true));
        }
        foreach (var ch in text)
        {
            inputs.Add(Key(0, ch, false, unicode: true));
            inputs.Add(Key(0, ch, true, unicode: true));
        }
        if (trailingVk != 0)
        {
            inputs.Add(Key(trailingVk, '\0', false));
            inputs.Add(Key(trailingVk, '\0', true));
        }
        var arr = inputs.ToArray();
        uint sent = NativeMethods.SendInput((uint)arr.Length, arr,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == arr.Length;
    }

    private static NativeMethods.INPUT Key(ushort vk, char ch, bool keyUp, bool unicode = false) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = unicode ? (ushort)0 : vk,
                wScan = unicode ? ch : (ushort)0,
                dwFlags = (unicode ? NativeMethods.KEYEVENTF_UNICODE : 0)
                          | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = NativeMethods.KRIGHT_MARKER,
            }
        }
    };

    private static void SendKey(ushort vk, char ch, bool keyUp, bool unicode = false)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = unicode ? (ushort)0 : vk,
                    wScan = unicode ? ch : (ushort)0,
                    dwFlags = (unicode ? NativeMethods.KEYEVENTF_UNICODE : 0)
                              | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
                    time = 0,
                    dwExtraInfo = NativeMethods.KRIGHT_MARKER,
                }
            }
        };
        NativeMethods.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
