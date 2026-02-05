using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using AvaloniaDemo;

internal sealed class Program
{
    public static Task Main(string[] args) => BuildAvaloniaApp().WithInterFont().StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>();
}
