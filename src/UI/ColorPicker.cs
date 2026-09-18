using System;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlowSync.Core;

namespace GlowSync.UI;

/// <summary>Compact HSV color picker bound to a "#RRGGBB" string.</summary>
internal sealed class ColorPicker : UserControl
{
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorPicker)d).OnHexChanged()));

    private static readonly string[] Presets =
    {
        "#FF0000", "#FF5A00", "#FFB300", "#FFF200", "#7CFF00", "#00FF6A",
        "#00FFE1", "#00A2FF", "#2B3BFF", "#8A2BFF", "#FF2BD6", "#FFFFFF",
    };

    private readonly GradientBar _hue = new();
    private readonly GradientBar _sat = new();
    private readonly GradientBar _val = new();
    private readonly Border _swatch = new() { Width = 44, Height = 44, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
    private readonly System.Windows.Controls.TextBox _hexBox = new() { Width = 110, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private bool _updating;
    private float _h, _s = 1, _v = 1;

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    public ColorPicker()
    {
        var hueStops = new GradientStopCollection();
        for (int i = 0; i <= 6; i++)
        {
            var c = ColorMath.Hsv(i / 6f, 1, 1);
            hueStops.Add(new GradientStop(ToColor(c), i / 6.0));
        }
        _hue.Stops = hueStops;
        _swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));

        _hue.ValueChanged += v => { _h = (float)v; Commit(); };
        _sat.ValueChanged += v => { _s = (float)v; Commit(); };
        _val.ValueChanged += v => { _v = (float)v; Commit(); };
        _hexBox.LostKeyboardFocus += (_, _) => ApplyHexText();
        _hexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) ApplyHexText();
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(_swatch);
        top.Children.Add(_hexBox);
        var presets = new WrapPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
        foreach (var preset in Presets)
        {
            var chip = new Border
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(ToColor(ColorMath.ParseHex(preset))),
                Cursor = Cursors.Hand,
                ToolTip = preset,
            };
            chip.MouseLeftButtonUp += (_, _) => Hex = preset;
            presets.Children.Add(chip);
        }
        top.Children.Add(presets);

        var root = new StackPanel { MinWidth = 300 };
        root.Children.Add(top);
        root.Children.Add(Labeled("Оттенок", _hue));
        root.Children.Add(Labeled("Насыщенность", _sat));
        root.Children.Add(Labeled("Яркость цвета", _val));
        Content = root;
        OnHexChanged();
    }

    private static FrameworkElement Labeled(string label, GradientBar bar)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var text = new System.Windows.Controls.TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        Grid.SetColumn(bar, 1);
        grid.Children.Add(text);
        grid.Children.Add(bar);
        return grid;
    }

    private void ApplyHexText()
    {
        var parsed = ColorMath.ParseHex(_hexBox.Text, new Vector3(-1));
        if (parsed.X >= 0) Hex = ColorMath.ToHex(parsed);
        else _hexBox.Text = Hex;
    }

    private void OnHexChanged()
    {
        if (_updating) return;
        var c = ColorMath.ParseHex(Hex, Vector3.One);
        var (h, s, v) = ColorMath.ToHsv(c);
        if (s > 0.001f && v > 0.001f) _h = h;
        if (v > 0.001f) _s = s;
        _v = v;
        Refresh(c);
    }

    private void Commit()
    {
        if (_updating) return;
        var c = ColorMath.Hsv(_h, _s, _v);
        _updating = true;
        Hex = ColorMath.ToHex(c);
        _updating = false;
        Refresh(c);
    }

    private void Refresh(Vector3 color)
    {
        _updating = true;
        _hue.Value = _h;
        _sat.Value = _s;
        _val.Value = _v;
        _sat.Stops = new GradientStopCollection { new(ToColor(new Vector3(_v)), 0), new(ToColor(ColorMath.Hsv(_h, 1, _v)), 1) };
        _val.Stops = new GradientStopCollection { new(Colors.Black, 0), new(ToColor(ColorMath.Hsv(_h, _s, 1)), 1) };
        _swatch.Background = new SolidColorBrush(ToColor(color));
        _hexBox.Text = ColorMath.ToHex(color);
        _updating = false;
    }

    private static Color ToColor(Vector3 c) => Color.FromRgb(ColorMath.ToByte(c.X), ColorMath.ToByte(c.Y), ColorMath.ToByte(c.Z));
}

/// <summary>Draggable gradient track with a round thumb; value 0..1.</summary>
internal sealed class GradientBar : FrameworkElement
{
    private double _value;
    private GradientStopCollection _stops = new();

    public event Action<double>? ValueChanged;

    public GradientBar()
    {
        Height = 22;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    public GradientStopCollection Stops
    {
        get => _stops;
        set
        {
            _stops = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        const double pad = 9;
        var track = new Rect(pad, h / 2 - 5, Math.Max(1, w - pad * 2), 10);
        // Transparent hit area across the whole control.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        dc.DrawRoundedRectangle(new LinearGradientBrush(_stops, 0), null, track, 5, 5);
        double x = track.Left + _value * track.Width;
        dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1.5), new Point(x, h / 2), 8, 8);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Update(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Update(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    private void Update(double x)
    {
        const double pad = 9;
        Value = (x - pad) / Math.Max(1, ActualWidth - pad * 2);
        ValueChanged?.Invoke(_value);
    }
}
