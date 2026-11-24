using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Blocker.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.UI.Render;

/// <summary>Builds the shell once and renders every scenario in the same process.</summary>
internal sealed class ScenarioRunner
{
    /// <summary>The frozen "now" of every render: Monday 23 November 2026, 10:30, UTC being the local time zone.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 30, 0, TimeSpan.Zero);

    private readonly RunOptions _options;
    private PluginWorld? _world;

    public ScenarioRunner(RunOptions options) => _options = options;

    /// <summary>Renders the selected scenarios.</summary>
    /// <param name="log">Receives one line per shot and a summary.</param>
    /// <returns>The number of failed shots.</returns>
    public async Task<int> RunAsync(Action<string> log)
    {
        Stopwatch total = Stopwatch.StartNew();
        ScenarioFile file = ScenarioFile.Load(_options.ScenarioPath);
        Scenario[] shots = [.. file.Shots.Where(s => _options.Only.Count == 0 || _options.Only.Contains(s.Name))];
        foreach (string missing in _options.Only.Where(n => file.Shots.All(s => s.Name != n)))
        {
            log($"unknown shot '{missing}'");
        }

        _ = Directory.CreateDirectory(_options.OutputDirectory);
        string pluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
        var time = new FakeTimeProvider(Now);
        _world = await PluginWorld.CreateAsync(pluginsDirectory, Now, CancellationToken.None);
        var connection = new StubServiceConnection(() => _world!);
        RenderShell shell = RenderShell.Create(connection, time);
        Window window = shell.Window;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;
        window.ShowInTaskbar = false;
        window.Show();
        await Interaction.SettleAsync(window);
        log($"startup {total.ElapsedMilliseconds} ms (shell and plugins ready)");

        int failed = 0;
        foreach (Scenario shot in shots)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                _world = await PluginWorld.CreateAsync(pluginsDirectory, Now, CancellationToken.None);
                connection.SetSession(shot.Session ?? "idle");
                await shell.RefreshPluginsAsync();
                if (ForestGallery.TryParse(shot.Target, out int galleryTurns, out double galleryHeight, out int galleryColumns, out string[] galleryIds))
                {
                    await RenderForestGalleryAsync(shot, galleryTurns, galleryHeight, galleryColumns, galleryIds);
                    log($"{shot.Name,-24} {watch.ElapsedMilliseconds,5} ms");
                    continue;
                }

                SizeWindow(window, shot.Size);
                await NavigateAsync(shell, shot.Target);
                Func<Task>? frame = shot.Animate ? StartFrames(window, shot.Name) : null;
                await (frame?.Invoke() ?? Task.CompletedTask);
                foreach (ScenarioStep step in shot.Steps ?? [])
                {
                    await ExecuteAsync(window, step, frame);
                    await (frame?.Invoke() ?? Task.CompletedTask);
                }

                await Interaction.SettleAsync(window);
                Save(window, Path.Combine(_options.OutputDirectory, shot.Name + ".png"));
                log($"{shot.Name,-24} {watch.ElapsedMilliseconds,5} ms");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or ArgumentException)
            {
                failed++;
                log($"{shot.Name,-24} FAILED: {ex.Message}");
            }
        }

        log($"total {total.ElapsedMilliseconds} ms for {shots.Length - failed} shots ({failed} failed)");
        window.Close();
        return failed;
    }

    private static void SizeWindow(Window window, int[]? size)
    {
        window.Width = size is { Length: 2 } ? size[0] : 1100;
        window.Height = size is { Length: 2 } ? size[1] : 700;
    }

    // The Focus Forest model sheet is not a plugin page: it is drawn in a window of its own so that every model can be reviewed at once.
    private async Task RenderForestGalleryAsync(Scenario shot, int turns, double height, int columns, string[] ids)
    {
        var gallery = new Window
        {
            Width = shot.Size is { Length: 2 } ? shot.Size[0] : 1900,
            Height = shot.Size is { Length: 2 } ? shot.Size[1] : 1800,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowInTaskbar = false,
            Background = Brushes.White,
            Content = new Blocker.UI.Controls.Rendering.UiNodeView { Node = ForestGallery.Build(turns, height, columns, ids) },
        };
        gallery.Show();
        await Interaction.SettleAsync(gallery);
        Save(gallery, Path.Combine(_options.OutputDirectory, shot.Name + ".png"));
        gallery.Close();
    }

    private async Task NavigateAsync(RenderShell shell, string target)
    {
        string[] parts = target.Split(':', 2);
        INavigator navigator = shell.Navigator;
        if (parts is ["plugin", var name])
        {
            LoadedPlugin plugin = _world!.Plugins.FirstOrDefault(p => p.Id.EndsWith("." + name, StringComparison.OrdinalIgnoreCase) || p.Folder.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown plugin '{name}'.");
            navigator.GoToPluginPage(plugin.Id);
        }
        else if (parts is ["main", var page] && Enum.TryParse(page, ignoreCase: true, out AppPage appPage))
        {
            navigator.GoTo(appPage);
        }
        else
        {
            throw new InvalidOperationException($"Unknown target '{target}'.");
        }

        await Interaction.SettleAsync(shell.Window);
        Interaction.ResetScroll(shell.Window);
        await Interaction.SettleAsync(shell.Window);
    }

    private Func<Task> StartFrames(Window window, string name)
    {
        string directory = Path.Combine(_options.OutputDirectory, "anim", name);
        Directory.CreateDirectory(directory);
        int index = 0;
        return async () =>
        {
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            Save(window, Path.Combine(directory, $"{index++:D3}.png"));
        };
    }

    private static async Task ExecuteAsync(Window window, ScenarioStep step, Func<Task>? frame)
    {
        if (step.Action is not null)
        {
            await Interaction.InvokeActionAsync(window, step);
        }
        else if (step.Click is { Length: 2 } click)
        {
            await Interaction.ClickAsync(window, click[0], click[1], frame);
        }
        else if (step.Drag is [{ Length: 2 } from, { Length: 2 } to])
        {
            await Interaction.DragAsync(window, from, to, frame);
        }
        else if (step.Move is { Length: 2 } move)
        {
            await Interaction.HoverAsync(window, move[0], move[1]);
        }
        else if (step.Wheel is { Length: 3 } wheel)
        {
            await Interaction.WheelAsync(window, wheel[0], wheel[1], (int)wheel[2]);
        }
        else if (step.Wait is { } wait)
        {
            await Task.Delay(wait);
            await Interaction.SettleAsync(window);
        }
        else
        {
            throw new InvalidOperationException("A step needs one of action, click, drag, move, wheel or wait.");
        }
    }

    // Rendering through a VisualBrush keeps the picture independent of where the window sits on the screen.
    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(window) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
