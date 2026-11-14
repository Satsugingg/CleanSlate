using System.Windows.Input;

namespace CleanSlate.Core;

/// <summary>
/// Parses, formats, and matches configurable keyboard shortcuts such as "/", "F3", or "Ctrl+F".
/// </summary>
public static class KeybindHelper
{
    public const string DefaultSearchShortcut = "/";
    public const string DefaultSummonShortcut = "Ctrl+Alt+Space";

    public static string Normalize(string? shortcut)
    {
        return TryParse(shortcut, out Key key, out ModifierKeys modifiers)
            ? Format(key, modifiers)
            : DefaultSearchShortcut;
    }

    public static string NormalizeSummonShortcut(string? shortcut)
    {
        if (shortcut is not null && string.IsNullOrWhiteSpace(shortcut))
        {
            return string.Empty;
        }

        if (shortcut is null)
        {
            return DefaultSummonShortcut;
        }

        return TryParseGlobalHotKey(shortcut, out _, out _)
            && TryParse(shortcut, out Key key, out ModifierKeys modifiers)
            ? Format(key, modifiers)
            : string.Empty;
    }

    public static bool TryParseGlobalHotKey(string? shortcut, out uint modifiersMask, out uint virtualKey)
    {
        modifiersMask = 0;
        virtualKey = 0;

        if (!TryParse(shortcut, out Key key, out ModifierKeys modifiers))
        {
            return false;
        }

        bool isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if (modifiers == ModifierKeys.None && !isFunctionKey)
        {
            return false;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0)
        {
            return false;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt)) modifiersMask |= 0x0001;
        if (modifiers.HasFlag(ModifierKeys.Control)) modifiersMask |= 0x0002;
        if (modifiers.HasFlag(ModifierKeys.Shift)) modifiersMask |= 0x0004;
        if (modifiers.HasFlag(ModifierKeys.Windows)) modifiersMask |= 0x0008;
        modifiersMask |= 0x4000; // MOD_NOREPEAT

        virtualKey = (uint)vk;
        return true;
    }

    public static bool TryParse(string? shortcut, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;

        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return false;
        }

        string trimmed = shortcut.Trim();
        if (string.Equals(trimmed, DefaultSearchShortcut, StringComparison.Ordinal))
        {
            key = Key.OemQuestion;
            modifiers = ModifierKeys.None;
            return true;
        }

        string[] parts = trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (TryParseModifierToken(parts[i], out ModifierKeys mod))
            {
                modifiers |= mod;
            }
            else
            {
                return false;
            }
        }

        string keyToken = parts[^1];
        return TryParseKeyToken(keyToken, out key);
    }

    public static string Format(Key key, ModifierKeys modifiers)
    {
        var tokens = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            tokens.Add("Ctrl");
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            tokens.Add("Alt");
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            tokens.Add("Shift");
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            tokens.Add("Win");
        }

        tokens.Add(FormatKeyToken(key, modifiers));
        return string.Join("+", tokens);
    }

    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin;

    public static bool IsDisallowedCaptureKey(Key key) =>
        key is Key.None or Key.Escape or Key.Enter or Key.Return or
            Key.Apps or Key.CapsLock or Key.NumLock or Key.Scroll or
            Key.DeadCharProcessed;

    public static bool Matches(string? configuredShortcut, Key pressedKey, ModifierKeys pressedModifiers)
    {
        if (!TryParse(configuredShortcut, out Key targetKey, out ModifierKeys targetMods))
        {
            targetKey = Key.OemQuestion;
            targetMods = ModifierKeys.None;
        }

        if (pressedKey == Key.Divide && targetKey == Key.OemQuestion && targetMods == ModifierKeys.None)
        {
            return pressedModifiers == ModifierKeys.None;
        }

        return pressedKey == targetKey && pressedModifiers == targetMods;
    }

    public static bool IsSlashTextTrigger(string? configuredShortcut, string? text, ModifierKeys pressedModifiers)
    {
        if (!TryParse(configuredShortcut, out Key targetKey, out ModifierKeys targetMods))
        {
            return false;
        }

        return targetKey == Key.OemQuestion &&
               targetMods == ModifierKeys.None &&
               string.Equals(text, "/", StringComparison.Ordinal) &&
               !pressedModifiers.HasFlag(ModifierKeys.Control) &&
               !pressedModifiers.HasFlag(ModifierKeys.Alt) &&
               !pressedModifiers.HasFlag(ModifierKeys.Windows);
    }

    private static bool TryParseModifierToken(string token, out ModifierKeys modifier)
    {
        if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("Control", StringComparison.OrdinalIgnoreCase))
        {
            modifier = ModifierKeys.Control;
            return true;
        }
        if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
        {
            modifier = ModifierKeys.Alt;
            return true;
        }
        if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            modifier = ModifierKeys.Shift;
            return true;
        }
        if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
        {
            modifier = ModifierKeys.Windows;
            return true;
        }

        modifier = ModifierKeys.None;
        return false;
    }

    private static bool TryParseKeyToken(string token, out Key key)
    {
        switch (token)
        {
            case "/":
                key = Key.OemQuestion;
                return true;
            case "-":
                key = Key.OemMinus;
                return true;
            case "=":
                key = Key.OemPlus;
                return true;
            case ".":
                key = Key.OemPeriod;
                return true;
            case ",":
                key = Key.OemComma;
                return true;
            case ";":
                key = Key.OemSemicolon;
                return true;
            case "'":
                key = Key.OemQuotes;
                return true;
            case "[":
                key = Key.OemOpenBrackets;
                return true;
            case "]":
                key = Key.OemCloseBrackets;
                return true;
            case "\\":
                key = Key.OemPipe;
                return true;
            case "`":
                key = Key.OemTilde;
                return true;
        }

        if (token.Length == 1 && char.IsDigit(token[0]))
        {
            key = Key.D0 + (token[0] - '0');
            return true;
        }

        if (Enum.TryParse(token, ignoreCase: true, out Key parsed) &&
            parsed != Key.None &&
            !IsModifierKey(parsed))
        {
            key = parsed;
            return true;
        }

        key = Key.None;
        return false;
    }

    private static string FormatKeyToken(Key key, ModifierKeys modifiers)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        return key switch
        {
            Key.OemQuestion or Key.Divide when modifiers == ModifierKeys.None => "/",
            Key.OemQuestion => "/",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemBackslash or Key.OemPipe => "\\",
            Key.OemTilde => "`",
            _ => key.ToString()
        };
    }
}
