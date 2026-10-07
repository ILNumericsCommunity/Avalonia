using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;

namespace AvaloniaDemo.Browser;

internal sealed class Program
{
    public static Task Main(string[] args) => BuildAvaloniaApp().WithInterFont().StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>();
}
