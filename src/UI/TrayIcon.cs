using System;
using System.Drawing;
using System.Windows.Forms;
using GlowSync.Config;
using GlowSync.Device;

namespace GlowSync.UI;

/// <summary>Notification-area icon. Left click opens settings; right click shows the quick menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppController _app;
    private readonly NotifyIcon _icon;
    private readonly Icon _onIcon;
    private readonly Icon _offIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _enabled;
    private readonly ToolStripMenuItem _modes;
    private readonly ToolStripMenuItem _brightness;
    private readonly ToolStripMenuItem _autostart;

    public TrayIcon(AppController app)
    {
        _app = app;
        _onIcon = LoadIcon("tray_on.ico");
        _offIcon = LoadIcon("tray_off.ico");

        _menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };
        _status = new ToolStripMenuItem { Enabled = false };
        _enabled = new ToolStripMenuItem("Подсветка включена", null, (_, _) => _app.ToggleEnabled());
        _modes = new ToolStripMenuItem("Режим");
        foreach (var mode in AppController.ModeOrder)
        {
            var item = new ToolStripMenuItem(SkydimoImporter.ModeName(mode)) { Tag = mode };
            item.Click += (_, _) => _app.SetMode(mode);
            _modes.DropDownItems.Add(item);
        }
        _brightness = new ToolStripMenuItem("Яркость");
        foreach (int level in new[] { 100, 80, 60, 40, 25, 10, 5 })
        {
            var item = new ToolStripMenuItem($"{level}%") { Tag = level };
            item.Click += (_, _) =>
            {
                _app.Store.Config.Brightness = level;
                _app.Store.Config.Enabled = true;
            };
            _brightness.DropDownItems.Add(item);
        }
        _autostart = new ToolStripMenuItem("Запускать вместе с Windows", null, (_, _) =>
            _app.Store.Config.Behavior.Autostart = !_app.Store.Config.Behavior.Autostart);

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _status,
            new ToolStripSeparator(),
            _enabled,
            _modes,
            _brightness,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Настройки…", null, (_, _) => _app.ShowSettings()) { Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold) },
            _autostart,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Выход", null, (_, _) => _app.Exit()),
        });
        foreach (ToolStripItem dropdown in new ToolStripItem[] { _modes, _brightness })
        {
            if (dropdown is ToolStripMenuItem m && m.DropDown is ToolStripDropDownMenu dd)
            {
                dd.Renderer = _menu.Renderer;
                dd.ShowImageMargin = false;
                dd.ShowCheckMargin = true;
            }
        }
        _menu.ShowCheckMargin = true;
        _menu.Opening += (_, _) => Refresh();

        _icon = new NotifyIcon { Icon = _onIcon, ContextMenuStrip = _menu, Visible = true, Text = "GlowSync" };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _app.ShowSettings();
        };
    }

    public void Refresh()
    {
        var cfg = _app.Store.Config;
        var device = _app.Device.Status;
        bool lit = _app.Engine.IsActive;

        _icon.Icon = lit && device.State == DeviceState.Connected ? _onIcon : _offIcon;
        string state = !cfg.Enabled ? "выключена"
            : _app.Engine.DisplayOff && cfg.Behavior.OffWhenDisplayOff ? "монитор выключен"
            : $"{SkydimoImporter.ModeName(cfg.Mode)}, {cfg.Brightness}%";
        var tip = $"GlowSync — {state}\n{device.Describe()}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;

        _status.Text = device.Describe();
        _enabled.Checked = cfg.Enabled;
        _autostart.Checked = cfg.Behavior.Autostart;
        foreach (ToolStripMenuItem item in _modes.DropDownItems) item.Checked = (EffectMode)item.Tag! == cfg.Mode;
        int nearest = 0, bestDiff = int.MaxValue;
        foreach (ToolStripMenuItem item in _brightness.DropDownItems)
        {
            int diff = Math.Abs((int)item.Tag! - cfg.Brightness);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                nearest = (int)item.Tag!;
            }
        }
        foreach (ToolStripMenuItem item in _brightness.DropDownItems) item.Checked = (int)item.Tag! == nearest;
        _brightness.Text = $"Яркость: {cfg.Brightness}%";
    }

    private static Icon LoadIcon(string name)
    {
        var info = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"));
        using var stream = info!.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _onIcon.Dispose();
        _offIcon.Dispose();
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Back = Color.FromArgb(32, 32, 36);
        private static readonly Color Hover = Color.FromArgb(55, 55, 62);
        private static readonly Color Text = Color.FromArgb(240, 240, 244);
        private static readonly Color Muted = Color.FromArgb(150, 150, 160);

        public DarkRenderer() : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var pen = new Pen(Color.FromArgb(120, 190, 255), 2f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
                new PointF(r.Left + r.Width * 0.82f, r.Top + r.Height * 0.3f),
            });
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Back;
            public override Color MenuBorder => Color.FromArgb(70, 70, 78);
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color ImageMarginGradientBegin => Back;
            public override Color ImageMarginGradientMiddle => Back;
            public override Color ImageMarginGradientEnd => Back;
            public override Color SeparatorDark => Color.FromArgb(60, 60, 66);
            public override Color SeparatorLight => Back;
            public override Color CheckBackground => Back;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }
    }
}
