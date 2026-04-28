using System.Reflection;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;

namespace WinGeminiWrapper;

internal static class AppIconProvider
{
    private const string ColorIconResourceName =
        "WinGeminiWrapper.Assets.Icons.WinGeminiIcon_color.png";
    private const string GrayIconResourceName =
        "WinGeminiWrapper.Assets.Icons.WinGeminiIcon_gray.png";
    private const int TraySpinFrameCount = 12;

    private static Icon? _appIcon;
    private static Icon? _trayColorIcon;
    private static Icon? _trayGrayIcon;
    private static IReadOnlyList<Icon>? _traySpinIcons;

    internal static Icon GetIcon()
    {
        if (_appIcon is not null)
        {
            return _appIcon;
        }

        _appIcon = LoadIconFromResource(ColorIconResourceName) ?? SystemIcons.Application;
        return _appIcon;
    }

    internal static Icon GetTrayActiveIcon()
    {
        if (_trayColorIcon is not null)
        {
            return _trayColorIcon;
        }

        _trayColorIcon = LoadIconFromResource(ColorIconResourceName) ?? GetIcon();
        return _trayColorIcon;
    }

    internal static Icon GetTrayIdleIcon()
    {
        if (_trayGrayIcon is not null)
        {
            return _trayGrayIcon;
        }

        _trayGrayIcon = LoadIconFromResource(GrayIconResourceName) ?? GetTrayActiveIcon();
        return _trayGrayIcon;
    }

    internal static IReadOnlyList<Icon> GetTraySpinIcons()
    {
        if (_traySpinIcons is not null)
        {
            return _traySpinIcons;
        }

        var source = LoadBitmapFromResource(ColorIconResourceName);
        if (source is null)
        {
            _traySpinIcons = [GetTrayActiveIcon()];
            return _traySpinIcons;
        }

        var frames = new List<Icon>(TraySpinFrameCount);
        for (var i = 0; i < TraySpinFrameCount; i++)
        {
            var angle = (360f * i) / TraySpinFrameCount;
            using var frameBitmap = BuildRotatedBitmap(source, angle);
            frames.Add(CreateIconFromBitmap(frameBitmap));
        }

        _traySpinIcons = frames;
        return _traySpinIcons;
    }

    private static Bitmap? LoadBitmapFromResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var bitmap = new Bitmap(stream);
        return new Bitmap(bitmap);
    }

    private static Icon? LoadIconFromResource(string resourceName)
    {
        using var bitmap = LoadBitmapFromResource(resourceName);
        if (bitmap is null)
        {
            return null;
        }

        return CreateIconFromBitmap(bitmap);
    }

    private static Bitmap BuildRotatedBitmap(Bitmap source, float angleDegrees)
    {
        var frame = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        frame.SetResolution(source.HorizontalResolution, source.VerticalResolution);

        using var graphics = Graphics.FromImage(frame);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        graphics.TranslateTransform(source.Width / 2f, source.Height / 2f);
        graphics.RotateTransform(angleDegrees);
        graphics.TranslateTransform(-source.Width / 2f, -source.Height / 2f);
        graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));

        return frame;
    }

    private static Icon CreateIconFromBitmap(Bitmap bitmap)
    {
        var iconHandle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(iconHandle).Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
