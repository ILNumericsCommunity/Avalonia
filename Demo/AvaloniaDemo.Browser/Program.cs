using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using AvaloniaDemo;

internal sealed partial class Program
{
    public static Task Main(string[] args)
    {
        return BuildAvaloniaApp()
               .WithInterFont()
               .StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>();
    }
}
