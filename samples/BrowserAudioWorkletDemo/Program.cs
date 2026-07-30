using Avalonia;
using Avalonia.Browser;

namespace BrowserAudioWorkletDemo;

internal static class Program
{
    public static Task Main(string[] args) => BuildAvaloniaApp()
        .StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .WithInterFont()
        .LogToTrace();
}
