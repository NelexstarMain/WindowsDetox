using System.Windows;
using Blocker.UI.Controls;

namespace Blocker.UI.Render;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        RunOptions options;
        try
        {
            options = RunOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // The same dictionaries as src/Blocker.UI/App.xaml; the theme must be merged before any page is created.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = BlockerTheme.ThemeUri });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Blocker.UI;component/Resources/Shared.xaml", UriKind.Absolute) });
        Motion.Apply(app.Resources);

        int exitCode = 0;
        _ = app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                exitCode = await new ScenarioRunner(options).RunAsync(Console.WriteLine) == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
            finally
            {
                app.Shutdown();
            }
        });
        _ = app.Run();
        return exitCode;
    }
}
