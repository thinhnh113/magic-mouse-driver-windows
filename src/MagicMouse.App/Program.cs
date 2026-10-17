namespace MagicMouse.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\MagicMouse.SingleInstance", out bool first);
        if (!first)
        {
            // Ask the running instance to show its window.
            NativeUi.PostShowMessage();
            return;
        }
        ApplicationConfiguration.Initialize();
        bool minimized = args.Contains("--minimized");
        Application.Run(new TrayContext(minimized));
    }
}
