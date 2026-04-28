using System.Reflection;
using System.Runtime.InteropServices;

namespace WinGeminiWrapper;

internal static class AppIconProvider
{
    private const string IconResourceName =
        "WinGeminiWrapper.Assets.Icons.gemini_sparkle_4g_512_lt_f94943af3be039176192d.png";

    private static Icon? _icon;

    internal static Icon GetIcon()
    {
        if (_icon is not null)
        {
            return _icon;
        }

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(IconResourceName);
        if (stream is null)
        {
            _icon = SystemIcons.Application;
            return _icon;
        }

        using var bitmap = new Bitmap(stream);
        var iconHandle = bitmap.GetHicon();
        try
        {
            _icon = (Icon)Icon.FromHandle(iconHandle).Clone();
            return _icon;
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
