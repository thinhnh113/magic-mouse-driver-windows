namespace MagicMouse.App;

/// <summary>The MagicMouse icon embedded in the exe (Assets\MagicMouse.ico).</summary>
internal static class AppIcon
{
    public static Icon Get(int size)
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("MagicMouse.ico")!;
        return new Icon(s, size, size);
    }
}
