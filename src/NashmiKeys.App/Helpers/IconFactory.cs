using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace NashmiKeys.App.Helpers;

public static class IconFactory
{
    private const int DesignSize = 32;

    public static Icon CreateTrayIcon()
    {
        var size = System.Windows.Forms.SystemInformation.SmallIconSize;
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                using var associated = Icon.ExtractAssociatedIcon(processPath);
                if (associated is not null)
                {
                    using var scaled = new Icon(associated, size);
                    return (Icon)scaled.Clone();
                }
            }
        }
        catch
        {
            // Fall back to the drawn icon below.
        }

        using var bitmap = DrawGamepad(size.Width);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static System.Windows.Media.ImageSource CreateWindowIcon()
    {
        using var bitmap = DrawGamepad(DesignSize);
        var handle = bitmap.GetHicon();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Bitmap DrawGamepad(int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var scale = size / (float)DesignSize;
        graphics.ScaleTransform(scale, scale);

        using var fill = new LinearGradientBrush(
            new Rectangle(0, 0, DesignSize, DesignSize),
            Color.FromArgb(255, 112, 156, 255),
            Color.FromArgb(255, 141, 107, 255),
            45f);

        // Grips plus the rounded central body make a recognizable gamepad.
        graphics.FillEllipse(fill, 1, 16, 12, 13);
        graphics.FillEllipse(fill, 19, 16, 12, 13);

        using (var body = new GraphicsPath())
        {
            AddRoundedRectangle(body, 4, 9, 24, 13, 6);
            graphics.FillPath(fill, body);
        }

        using var white = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
        using var dark = new SolidBrush(Color.FromArgb(150, 22, 28, 42));

        graphics.FillRectangle(white, 9.5f, 13.7f, 5.4f, 1.8f);
        graphics.FillRectangle(white, 11.3f, 11.9f, 1.8f, 5.4f);
        graphics.FillEllipse(white, 20.8f, 13.0f, 2.3f, 2.3f);
        graphics.FillEllipse(white, 23.0f, 15.0f, 2.3f, 2.3f);

        graphics.FillEllipse(dark, 7.6f, 17.4f, 6.2f, 6.2f);
        graphics.FillEllipse(dark, 18.2f, 17.4f, 6.2f, 6.2f);

        return bitmap;
    }

    private static void AddRoundedRectangle(GraphicsPath path, float x, float y, float width, float height, float radius)
    {
        var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
