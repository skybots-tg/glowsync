using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using GlowSync.Config;

namespace GlowSync.UI;

/// <summary>Text box that records a key combination. Backspace/Delete/Esc clears it.</summary>
internal sealed class HotkeyBox : System.Windows.Controls.TextBox
{
    private readonly HotkeyBinding _binding;
    private readonly AppController _app;

    public HotkeyBox(HotkeyBinding binding, AppController app)
    {
        _binding = binding;
        _app = app;
        // A subclass does not pick up the implicit (themed) TextBox style on its own.
        SetResourceReference(StyleProperty, typeof(System.Windows.Controls.TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        MinWidth = 220;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
        ToolTip = "Нажмите сочетание клавиш. Backspace — очистить.";
        UpdateText();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        // Otherwise the global hotkey would fire instead of being recorded.
        _app.SuspendHotkeys(true);
        Text = "Нажмите сочетание…";
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _app.SuspendHotkeys(false);
        UpdateText();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }
        if (key == Key.Tab)
        {
            e.Handled = false;
            return;
        }

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete or Key.Escape)
        {
            _binding.Key = 0;
            _binding.Modifiers = 0;
        }
        else
        {
            int flags = 0;
            if (mods.HasFlag(ModifierKeys.Alt)) flags |= HotkeyBinding.ModAlt;
            if (mods.HasFlag(ModifierKeys.Control)) flags |= HotkeyBinding.ModControl;
            if (mods.HasFlag(ModifierKeys.Shift)) flags |= HotkeyBinding.ModShift;
            if (mods.HasFlag(ModifierKeys.Windows)) flags |= HotkeyBinding.ModWin;
            // Plain letters without modifiers would hijack normal typing everywhere.
            if (flags == 0 && !(key >= Key.F1 && key <= Key.F24) && key is not (Key.Pause or Key.Scroll))
            {
                Text = "Нужен Ctrl, Alt, Shift или Win";
                return;
            }
            _binding.Modifiers = flags;
            _binding.Key = KeyInterop.VirtualKeyFromKey(key);
        }
        UpdateText();
        // Leave the box so the new combination is registered right away.
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void UpdateText() => Text = Describe(_binding.Modifiers, _binding.Key);

    public static string Describe(int modifiers, int vk)
    {
        if (vk == 0) return "Не назначено";
        var parts = new List<string>();
        if ((modifiers & HotkeyBinding.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & HotkeyBinding.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & HotkeyBinding.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & HotkeyBinding.ModWin) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey(vk);
        parts.Add(key switch
        {
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Left => "←",
            Key.Right => "→",
            Key.PageUp => "PgUp",
            Key.Next => "PgDn",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            _ => key.ToString(),
        });
        return string.Join(" + ", parts);
    }
}
