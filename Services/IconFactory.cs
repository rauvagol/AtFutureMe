using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace AtFutureMe.Services;

public static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateTrayIcon()
    {
        using var bmp = CreateBitmap();
        var handle = bmp.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var clone = (Icon)temp.Clone();
        DestroyIcon(handle);
        return clone;
    }

    public static BitmapSource CreateWindowIcon()
    {
        using var bmp = CreateBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    static Bitmap CreateBitmap()
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var bg = new SolidBrush(Color.FromArgb(255, 32, 34, 40));
        g.FillEllipse(bg, 1, 1, 30, 30);
        using var pen = new Pen(Color.FromArgb(255, 140, 200, 255), 2.4f);
        g.DrawEllipse(pen, 3, 3, 26, 26);
        using var check = new Pen(Color.FromArgb(255, 110, 220, 150), 3f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLines(check, [
            new Point(9, 16),
            new Point(14, 22),
            new Point(23, 11)
        ]);
        return bmp;
    }
}
