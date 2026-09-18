using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GlowSync.Config;

namespace GlowSync.UI;

/// <summary>
/// Small on-screen hint for hotkeys ("Яркость 70%"). Click-through, never takes focus,
/// never appears in the taskbar or Alt+Tab.
/// </summary>
internal static class Osd
{
    private static Window? _window;
    private static TextBlock? _title;
    private static Border? _bar;
    private static Border? _barFill;
    private static DispatcherTimer? _hide;

    public static void Show(HotkeyAction action, AppConfig cfg)
    {
        EnsureWindow();
        bool brightness = action is HotkeyAction.BrightnessUp or HotkeyAction.BrightnessDown;
        _title!.Text = action switch
        {
            HotkeyAction.Toggle => cfg.Enabled ? "Подсветка включена" : "Подсветка выключена",
            HotkeyAction.BrightnessUp or HotkeyAction.BrightnessDown => $"Яркость {cfg.Brightness}%",
            _ => "Режим: " + SkydimoImporter.ModeName(cfg.Mode),
        };
        _bar!.Visibility = brightness ? Visibility.Visible : Visibility.Collapsed;
        _barFill!.Width = 216 * Math.Clamp(cfg.Brightness, 0, 100) / 100.0;

        var area = SystemParameters.WorkArea;
        _window!.Left = area.Left + (area.Width - _window.Width) / 2;
        _window.Top = area.Bottom - _window.Height - 80;
        _window.BeginAnimation(UIElement.OpacityProperty, null);
        _window.Opacity = 1;
        _window.Show();
        _hide!.Stop();
        _hide.Start();
    }

    private static void EnsureWindow()
    {
        if (_window != null) return;
        _title = new TextBlock { Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        _barFill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(96, 205, 255)) };
        _bar = new Border { Height = 4, Width = 216, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 10, 0, 0), Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), Child = _barFill };
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(_title);
        panel.Children.Add(_bar);
        _window = new Window
        {
            Width = 260,
            Height = 76,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            ResizeMode = ResizeMode.NoResize,
            Content = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(235, 28, 28, 32)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(22, 12, 22, 12),
                Child = panel,
            },
        };
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        };
        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) =>
            {
                if (_window.Opacity <= 0.01) _window.Hide();
            };
            _window.BeginAnimation(UIElement.OpacityProperty, fade);
        };
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
