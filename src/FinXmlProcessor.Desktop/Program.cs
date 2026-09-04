using Avalonia;

namespace FinXmlProcessor.Desktop;

public static class Program
{
    // Avalonia configuration; also used by the previewer. Do not use Avalonia APIs before AppMain.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
