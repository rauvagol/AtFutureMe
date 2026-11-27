using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AtFutureMe.Models;

namespace AtFutureMe.Windows;

/// <summary>
/// Paints each task wedge into its own layer, blends those layers, then the result
/// is placed on the clock in normal mode so the face still shows through.
/// </summary>
sealed class WedgeLayerCompositor
{
    readonly ClockWedgeBlend _mode;
    readonly int _width;
    readonly int _height;
    readonly double _dpiX;
    readonly double _dpiY;
    readonly double _dipWidth;
    readonly double _dipHeight;
    readonly byte[] _acc;
    DrawingVisual? _layer;
    DrawingContext? _layerDc;

    WedgeLayerCompositor(ClockWedgeBlend mode, int width, int height, double dpiX, double dpiY, double dipWidth, double dipHeight)
    {
        _mode = mode;
        _width = width;
        _height = height;
        _dpiX = dpiX;
        _dpiY = dpiY;
        _dipWidth = dipWidth;
        _dipHeight = dipHeight;
        _acc = new byte[width * height * 4];
    }

    public static WedgeLayerCompositor? TryCreate(FrameworkElement face, ClockWedgeBlend mode)
    {
        if (mode == ClockWedgeBlend.Normal)
            return null;
        var dipW = face.ActualWidth;
        var dipH = face.ActualHeight;
        if (dipW < 2 || dipH < 2)
            return null;
        var dpi = VisualTreeHelper.GetDpi(face);
        var width = Math.Max(1, (int)Math.Ceiling(dipW * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(dipH * dpi.DpiScaleY));
        return new WedgeLayerCompositor(mode, width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, dipW, dipH);
    }

    public bool LayerOpen => _layerDc != null;

    public void BeginLayer()
    {
        EndLayer();
        _layer = new DrawingVisual();
        _layerDc = _layer.RenderOpen();
    }

    public void Draw(Geometry geometry, Brush fill, Brush? mask)
    {
        if (_layerDc == null)
            return;
        if (mask != null)
            _layerDc.PushOpacityMask(mask);
        _layerDc.DrawGeometry(fill, null, geometry);
        if (mask != null)
            _layerDc.Pop();
    }

    public void EndLayer()
    {
        if (_layerDc == null || _layer == null)
            return;
        _layerDc.Close();
        _layerDc = null;
        var rtb = new RenderTargetBitmap(_width, _height, _dpiX, _dpiY, PixelFormats.Pbgra32);
        rtb.Render(_layer);
        _layer = null;
        var stride = _width * 4;
        var src = new byte[stride * _height];
        rtb.CopyPixels(src, stride, 0);
        BlendOnto(src);
    }

    public Image CreateImage()
    {
        EndLayer();
        var bmp = new WriteableBitmap(_width, _height, _dpiX, _dpiY, PixelFormats.Pbgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, _width, _height), _acc, _width * 4, 0);
        bmp.Freeze();
        return new Image
        {
            Source = bmp,
            Width = _dipWidth,
            Height = _dipHeight,
            IsHitTestVisible = false
        };
    }

    void BlendOnto(byte[] src)
    {
        var n = _acc.Length;
        for (var i = 0; i < n; i += 4)
        {
            var sa = src[i + 3] / 255.0;
            if (sa <= 0)
                continue;
            var sb = src[i] / 255.0;
            var sg = src[i + 1] / 255.0;
            var sr = src[i + 2] / 255.0;
            var da = _acc[i + 3] / 255.0;
            var db = _acc[i] / 255.0;
            var dg = _acc[i + 1] / 255.0;
            var dr = _acc[i + 2] / 255.0;
            var Sr = sr / sa;
            var Sg = sg / sa;
            var Sb = sb / sa;
            double Dr = 0, Dg = 0, Db = 0;
            if (da > 0)
            {
                Dr = dr / da;
                Dg = dg / da;
                Db = db / da;
            }
            var Br = da <= 0 ? Sr : Mix(_mode, Dr, Sr);
            var Bg = da <= 0 ? Sg : Mix(_mode, Dg, Sg);
            var Bb = da <= 0 ? Sb : Mix(_mode, Db, Sb);
            var ao = sa + da * (1 - sa);
            var or = sa * (1 - da) * Sr + sa * da * Br + da * (1 - sa) * Dr;
            var og = sa * (1 - da) * Sg + sa * da * Bg + da * (1 - sa) * Dg;
            var ob = sa * (1 - da) * Sb + sa * da * Bb + da * (1 - sa) * Db;
            _acc[i] = ToByte(ob);
            _acc[i + 1] = ToByte(og);
            _acc[i + 2] = ToByte(or);
            _acc[i + 3] = ToByte(ao);
        }
    }

    static double Mix(ClockWedgeBlend mode, double dst, double src) => mode switch
    {
        ClockWedgeBlend.Multiply => dst * src,
        ClockWedgeBlend.Screen => 1 - (1 - dst) * (1 - src),
        ClockWedgeBlend.Overlay => dst < 0.5 ? 2 * dst * src : 1 - 2 * (1 - dst) * (1 - src),
        ClockWedgeBlend.Darken => Math.Min(dst, src),
        ClockWedgeBlend.Add => Math.Min(1, dst + src),
        _ => src
    };

    static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
}
