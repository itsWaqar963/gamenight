// Brand icon from embedded ico/icon.ico — used by tray, windows, and the exe.
using System.Reflection;

namespace GameNight.Agent;

public static class AppIcon
{
    private static Icon? _app;
    private static Icon? _tray;

    /// <summary>Window / taskbar icon (32px preferred).</summary>
    public static Icon ForWindow => _app ??= Load(32);

    /// <summary>NotifyIcon tray glyph (16px preferred).</summary>
    public static Icon ForTray => _tray ??= Load(16);

    private static Icon Load(int size)
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("gamenight.icon.ico");
            if (stream != null)
                return new Icon(stream, size, size);
        }
        catch { /* fall through */ }

        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted != null)
                    return new Icon(extracted, size, size);
            }
        }
        catch { /* fall through */ }

        return (Icon)SystemIcons.Application.Clone();
    }
}
