using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ImageSource = System.Windows.Media.ImageSource;

namespace KsefMonitor;

internal static class TrayIconFactory
{
    private const string ResourceName = "KsefMonitor.Assets.KSeFMonitor.ico";

    public static Icon Create()
    {
        using var stream = typeof(TrayIconFactory).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return CreateFallback();
        using var embedded = new Icon(stream, new Size(32, 32));
        return (Icon)embedded.Clone();
    }

    public static ImageSource CreateImageSource()
    {
        using var icon = Create();
        return CreateImageSource(icon);
    }

    public static ImageSource CreateImageSource(Icon icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            System.Windows.Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));
        source.Freeze();
        return source;
    }

    private static Icon CreateFallback()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(Color.FromArgb(20, 73, 122)))
        using (var foreground = new SolidBrush(Color.White))
        using (var font = new Font("Segoe UI", 8.2f, FontStyle.Bold, GraphicsUnit.Point))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            graphics.Clear(Color.Transparent);
            FillRoundedRectangle(graphics, background, new RectangleF(1, 1, 30, 30), 6);

            const string text = "KSeF";
            var size = graphics.MeasureString(text, font);
            graphics.DrawString(text, font, foreground, (32 - size.Width) / 2f, (32 - size.Height) / 2f - 0.5f);
        }

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

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
