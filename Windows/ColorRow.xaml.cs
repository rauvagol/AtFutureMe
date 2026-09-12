using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

public partial class ColorRow : UserControl
{
    bool _loading;
    string _fallback = ColorText.Idle;
    bool _allowClear;

    public string? OverrideHex { get; private set; }

    public event EventHandler? ColorCommitted;

    public ColorRow()
    {
        InitializeComponent();
    }

    public void Bind(string label, string fallback, string? overrideHex, bool allowClear)
    {
        _loading = true;
        _fallback = ColorText.NormalizeOr(fallback, ColorText.Idle);
        _allowClear = allowClear;
        OverrideHex = ColorText.TryNormalize(overrideHex, out var hex) ? hex : null;
        LabelText.Text = label;
        ClearButton.Visibility = allowClear ? Visibility.Visible : Visibility.Collapsed;
        HexBox.Text = OverrideHex ?? (allowClear ? "" : _fallback);
        Paint(OverrideHex ?? _fallback);
        _loading = false;
    }

    public void SetLabel(string label) => LabelText.Text = label;

    public void SetFallback(string fallback)
    {
        _fallback = ColorText.NormalizeOr(fallback, ColorText.Idle);
        if (OverrideHex == null)
            Paint(_fallback);
    }

    void Paint(string hex)
    {
        try
        {
            Swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        }
        catch
        {
            Swatch.Background = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
        }
    }

    void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        var text = HexBox.Text.Trim();
        if (text.Length is not (4 or 7))
            return;
        if (!ColorText.TryNormalize(text, out var hex))
            return;
        OverrideHex = hex;
        Paint(hex);
        ColorCommitted?.Invoke(this, EventArgs.Empty);
    }

    void OnHexLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var text = HexBox.Text.Trim();
        if (_allowClear && string.IsNullOrWhiteSpace(text))
        {
            OverrideHex = null;
            HexBox.Text = "";
            Paint(_fallback);
            ColorCommitted?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (ColorText.TryNormalize(text, out var hex))
        {
            OverrideHex = hex;
            HexBox.Text = hex;
            Paint(hex);
            ColorCommitted?.Invoke(this, EventArgs.Empty);
            return;
        }

        _loading = true;
        HexBox.Text = OverrideHex ?? (_allowClear ? "" : _fallback);
        Paint(OverrideHex ?? _fallback);
        _loading = false;
    }

    void OnPick(object sender, RoutedEventArgs e)
    {
        var shown = OverrideHex ?? _fallback;
        Color wpf;
        try
        {
            wpf = (Color)ColorConverter.ConvertFromString(shown)!;
        }
        catch
        {
            wpf = Color.FromRgb(0xB3, 0xB3, 0xB3);
        }

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(wpf.R, wpf.G, wpf.B)
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;
        var hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        OverrideHex = hex;
        _loading = true;
        HexBox.Text = hex;
        Paint(hex);
        _loading = false;
        ColorCommitted?.Invoke(this, EventArgs.Empty);
    }

    void OnClear(object sender, RoutedEventArgs e)
    {
        OverrideHex = null;
        _loading = true;
        HexBox.Text = "";
        Paint(_fallback);
        _loading = false;
        ColorCommitted?.Invoke(this, EventArgs.Empty);
    }
}
