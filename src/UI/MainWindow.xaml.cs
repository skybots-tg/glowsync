using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlowSync.Capture;
using GlowSync.Config;
using GlowSync.Core;
using GlowSync.Device;
using GlowSync.Platform;
using Wpf.Ui.Controls;

namespace GlowSync.UI;

internal sealed record ModeItem(EffectMode Mode, string Name, SymbolRegular Symbol, string Hint);

internal sealed record Option<T>(T Value, string Name);

internal sealed record PortItem(string Name, string Label);

public partial class MainWindow : FluentWindow
{
    private static readonly string[] LayoutProperties =
    {
        nameof(LayoutConfig.Left), nameof(LayoutConfig.Top), nameof(LayoutConfig.Right), nameof(LayoutConfig.Bottom),
        nameof(LayoutConfig.Start), nameof(LayoutConfig.Clockwise), nameof(LayoutConfig.Offset),
    };

    private readonly AppController _app;
    private readonly DispatcherTimer _timer;
    private byte[] _previewBuffer = Array.Empty<byte>();

    internal MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();

        ModeList.ItemsSource = new List<ModeItem>
        {
            new(EffectMode.Screen, "Экран", SymbolRegular.Desktop24, "Цвета с краёв экрана"),
            new(EffectMode.Music, "Музыка", SymbolRegular.MusicNote224, "Спектр, пульс, эквалайзер"),
            new(EffectMode.Rainbow, "Радуга", SymbolRegular.Sparkle24, "Бегущая радуга"),
            new(EffectMode.ColorCycle, "Переливание", SymbolRegular.ArrowSync24, "Плавная смена цвета"),
            new(EffectMode.Breathing, "Дыхание", SymbolRegular.HeartPulse24, "Мягкая пульсация"),
            new(EffectMode.Static, "Один цвет", SymbolRegular.Lightbulb24, "Постоянная подсветка"),
            new(EffectMode.Comet, "Комета", SymbolRegular.Rocket24, "Бегущий огонёк"),
        };
        MusicStyleBox.ItemsSource = new[]
        {
            new Option<MusicStyle>(MusicStyle.Spectrum, "Спектр"),
            new Option<MusicStyle>(MusicStyle.Pulse, "Пульс"),
            new Option<MusicStyle>(MusicStyle.Meter, "Уровень (VU)"),
        };
        ProtocolBox.ItemsSource = new[]
        {
            new Option<DeviceProtocol>(DeviceProtocol.Skydimo, "Skydimo"),
            new Option<DeviceProtocol>(DeviceProtocol.Adalight, "Adalight"),
        };
        BaudBox.ItemsSource = new[] { 115200, 230400, 460800, 500000, 921600, 1000000, 2000000 };
        StartBox.ItemsSource = new[]
        {
            new Option<StartCorner>(StartCorner.BottomLeft, "Снизу слева"),
            new Option<StartCorner>(StartCorner.TopLeft, "Сверху слева"),
            new Option<StartCorner>(StartCorner.TopRight, "Сверху справа"),
            new Option<StartCorner>(StartCorner.BottomRight, "Снизу справа"),
        };
        DirectionBox.ItemsSource = new[]
        {
            new Option<bool>(true, "По часовой стрелке"),
            new Option<bool>(false, "Против часовой"),
        };
        AboutText.Text = $"GlowSync {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";

        BindConfig();
        LoadMonitors();
        _ = LoadPortsAsync();

        _app.StatusChanged += UpdateStatus;
        _app.Store.Changed += OnConfigChanged;
        _app.Store.Replaced += BindConfig;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Nav.SelectedIndex = 0;
        UpdateStatus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _app.SuspendHotkeys(false);
        _app.StatusChanged -= UpdateStatus;
        _app.Store.Changed -= OnConfigChanged;
        _app.Store.Replaced -= BindConfig;
        base.OnClosed(e);
    }

    private void BindConfig()
    {
        var cfg = _app.Store.Config;
        DataContext = cfg;
        BuildHotkeys(cfg);
        UpdateLayoutInfo();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: string page }) return;
        foreach (var name in new[] { "PageHome", "PageScreen", "PageEffects", "PageColor", "PageStrip", "PageSettings" })
        {
            if (FindName(name) is ScrollViewer sv) sv.Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
        }
        if (page == "PageSettings")
        {
            UpdateSkydimoState();
            UpdateDdcState();
        }
        if (page == "PageStrip") _ = LoadPortsAsync();
    }

    private void Tick()
    {
        if (WindowState == WindowState.Minimized) return;
        if (PageSettings.Visibility == Visibility.Visible)
        {
            UpdateDdcState();
            return;
        }
        if (PageHome.Visibility != Visibility.Visible) return;
        int n = _app.Engine.CopyPreview(ref _previewBuffer);
        Preview.SetColors(_previewBuffer, n);
        FpsText.Text = _app.Engine.IsActive && _app.Engine.Fps > 0 ? $"{_app.Engine.Fps:0} к/с" : "";
    }

    private void OnConfigChanged(object sender, string property)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnConfigChanged(sender, property));
            return;
        }
        if (sender is LayoutConfig layout)
        {
            if (LayoutProperties.Contains(property)) LayoutBuilder.Apply(layout);
            UpdateLayoutInfo();
        }
        else if (sender is AppConfig || sender is BehaviorConfig)
        {
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        var status = _app.Device.Status;
        var cfg = _app.Store.Config;
        string output = !cfg.Enabled ? "выключена"
            : _app.Engine.DisplayOff && cfg.Behavior.OffWhenDisplayOff ? "монитор выключен — лента погашена"
            : "работает";
        DeviceText.Text = $"{status.Describe()}  ·  {output}";
        StripDeviceText.Text = status.State == DeviceState.Connected
            ? $"Подключено: {status.Model ?? "Adalight"}{(status.Serial != null ? $", S/N {status.Serial}" : "")} на {status.Port}, {status.LedCount} светодиодов"
            : status.Describe();
        SideStatus.Text = status.State == DeviceState.Connected ? $"● {status.Port} подключено" : "○ " + status.Describe();

        bool busy = status.State == DeviceState.PortBusy || (status.State != DeviceState.Connected && SkydimoApp.IsRunning);
        SkydimoBar.IsOpen = busy;
        SkydimoBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SkydimoBarButton.Visibility = busy && SkydimoApp.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayoutInfo();
    }

    private void UpdateLayoutInfo()
    {
        var layout = _app.Store.Config.Layout;
        var aspect = SystemParameters.PrimaryScreenHeight > 0 ? SystemParameters.PrimaryScreenWidth / SystemParameters.PrimaryScreenHeight : 16.0 / 9.0;
        Preview.SetLayout(layout.Leds, layout.GridWidth, layout.GridHeight, aspect);
        LayoutSummary.Text = $"Всего {layout.Leds.Count} светодиодов, сетка захвата {layout.GridWidth}×{layout.GridHeight}";

        int deviceCount = _app.Device.DeviceLedCount;
        bool mismatch = deviceCount > 0 && deviceCount != layout.Leds.Count;
        LedMismatchBar.IsOpen = mismatch;
        LedMismatchBar.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
        LedMismatchBar.Message = $"Контроллер ожидает {deviceCount}, а в раскладке {layout.Leds.Count}. Лишние будут отброшены, недостающие — погашены.";

        ImportText.Text = SkydimoImporter.IsAvailable
            ? (_app.Store.Config.ImportedFrom != null ? $"Уже импортировано: {_app.Store.Config.ImportedFrom}. Можно повторить." : "Найдены настройки Skydimo на этом компьютере")
            : "Настройки Skydimo не найдены";
        ImportButton.IsEnabled = SkydimoImporter.IsAvailable;
    }

    private void BuildHotkeys(AppConfig cfg)
    {
        HotkeyPanel.Children.Clear();
        var names = new Dictionary<HotkeyAction, (string, SymbolRegular)>
        {
            [HotkeyAction.Toggle] = ("Включить / выключить подсветку", SymbolRegular.Power24),
            [HotkeyAction.BrightnessUp] = ("Ярче", SymbolRegular.WeatherSunny24),
            [HotkeyAction.BrightnessDown] = ("Темнее", SymbolRegular.WeatherMoon24),
            [HotkeyAction.NextMode] = ("Следующий режим", SymbolRegular.ArrowRepeatAll24),
            [HotkeyAction.PrevMode] = ("Предыдущий режим", SymbolRegular.ArrowSync24),
        };
        foreach (var binding in cfg.Hotkeys.OrderBy(h => h.Action))
        {
            var (title, symbol) = names.TryGetValue(binding.Action, out var n) ? n : (binding.Action.ToString(), SymbolRegular.Keyboard24);
            HotkeyPanel.Children.Add(new CardControl
            {
                Icon = new SymbolIcon { Symbol = symbol },
                Header = new System.Windows.Controls.TextBlock { Text = title, Style = (Style)FindResource("RowTitle") },
                Content = new HotkeyBox(binding, _app),
            });
        }
        var conflicts = _app.HotkeyConflicts;
        HotkeyConflictText.Text = conflicts.Count == 0 ? "" : "Эти сочетания уже заняты другой программой: " + string.Join(", ", conflicts.Select(c => names[c].Item1));
        HotkeyConflictText.Visibility = conflicts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadMonitors()
    {
        var items = new List<MonitorInfo> { new("", 0, 0, true, "") };
        items.AddRange(DesktopCapture.ListMonitors());
        MonitorBox.ItemsSource = items.Select(m => new { m.DeviceName, Label = m.DeviceName == "" ? "Основной монитор" : m.Label }).ToList();
    }

    private async Task LoadPortsAsync()
    {
        var ports = await Task.Run(PortEnumerator.GetPorts);
        var items = new List<PortItem> { new(DeviceConfig.AutoPort, "Автоматически (рекомендуется)") };
        items.AddRange(ports.Select(p => new PortItem(p.Name, $"{p.Name} — {p.Description}")));
        var current = _app.Store.Config.Device.Port;
        if (!items.Any(i => string.Equals(i.Name, current, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(new PortItem(current, $"{current} — не подключён"));
        }
        PortBox.ItemsSource = items;
        PortBox.SelectedValue = items.First(i => string.Equals(i.Name, current, StringComparison.OrdinalIgnoreCase)).Name;
    }

    private void UpdateDdcState()
    {
        var monitor = _app.Engine.Monitor;
        DdcStateText.Text = !_app.Store.Config.Behavior.DetectMonitorViaDdc ? "Опрос выключен"
            : !monitor.Supported ? "Монитор пока не отвечал — выключение кнопкой определить не получится"
            : monitor.IsOff == true ? "Монитор отвечает: выключен"
            : $"Монитор отвечает: включён (код {monitor.LastValue})";
    }

    private void UpdateSkydimoState()
    {
        bool running = SkydimoApp.IsRunning, autostart = SkydimoApp.InAutostart, backup = SkydimoApp.HasBackup;
        SkydimoStateText.Text = (running ? "Сейчас запущен. " : "Не запущен. ")
                                + (autostart ? "Стоит в автозапуске — при входе в Windows займёт порт ленты." : "В автозапуске нет.");
        RetireButton.IsEnabled = running || autostart;
        RestoreButton.Visibility = backup ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RetireSkydimo_Click(object sender, RoutedEventArgs e)
    {
        SkydimoApp.Retire();
        _app.Device.RequestRescan();
        UpdateSkydimoState();
        UpdateStatus();
    }

    private void RestoreSkydimo_Click(object sender, RoutedEventArgs e)
    {
        SkydimoApp.RestoreAutostart();
        UpdateSkydimoState();
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadPortsAsync();
        _app.Device.RequestRescan();
    }

    private void TestSides_Click(object sender, RoutedEventArgs e) => _app.Engine.StartTest(TestPattern.Sides, 15);

    private void TestWhite_Click(object sender, RoutedEventArgs e) => _app.Engine.StartTest(TestPattern.White, 15);

    private void ResetBalance_Click(object sender, RoutedEventArgs e)
    {
        var color = _app.Store.Config.Color;
        color.RedGain = color.GreenGain = color.BlueGain = 100;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = _app.ImportSkydimo();
            ImportText.Text = "Импортировано: " + string.Join("; ", report);
        }
        catch (Exception ex)
        {
            Log.Error("Import failed", ex);
            ImportText.Text = "Не удалось импортировать: " + ex.Message;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.DataDir}\"") { UseShellExecute = true });
    }
}
