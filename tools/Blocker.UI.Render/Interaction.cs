using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Blocker.UI.Behaviors;
using Blocker.UI.Controls.Rendering;
using Blocker.UI.Sdk;
using Blocker.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.Render;

/// <summary>Executes scenario steps against the shown window.</summary>
internal static partial class Interaction
{
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;

    /// <summary>Waits until pending dispatcher work (bindings, layout, async commands that already completed) has run.</summary>
    /// <param name="window">The shown window.</param>
    /// <returns>A task that completes when the UI is idle.</returns>
    public static async Task SettleAsync(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
    }

    /// <summary>Sends a UI action through the command of the <see cref="UiNodeView"/> that shows the element, the path a real click takes.</summary>
    /// <param name="window">The shown window.</param>
    /// <param name="step">The step with the action.</param>
    /// <returns>A task that completes when the command and the refresh after it finished.</returns>
    public static async Task InvokeActionAsync(Window window, ScenarioStep step)
    {
        string id = step.Action!;
        UiAction action = CreateAction(id, step);
        string marker = $"\"{id}\"";
        foreach (UiNodeView view in Descendants<UiNodeView>(window))
        {
            if (view.Node is null || !UiJson.Serialize(view.Node).Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            if (UiNodeActions.GetCommand(view) is not { } command || UiNodeActions.GetContribution(view) is not { } contribution)
            {
                throw new InvalidOperationException($"The view showing '{id}' has no command; is the page shown?");
            }

            command.Execute(new ContributionAction(contribution, action));
            if (command is IAsyncRelayCommand { ExecutionTask: { } running })
            {
                await running;
            }

            await SettleAsync(window);
            return;
        }

        throw new InvalidOperationException($"No visible UI node view contains an element with id '{id}'.");
    }

    /// <summary>Injects a real left click at window coordinates.</summary>
    /// <param name="window">The shown window.</param>
    /// <param name="x">X in device-independent pixels from the window's top-left corner.</param>
    /// <param name="y">Y in device-independent pixels from the window's top-left corner.</param>
    /// <returns>A task that completes when the click was processed.</returns>
    public static async Task ClickAsync(Window window, double x, double y)
    {
        await MoveAsync(window, x, y);
        mouse_event(LeftDown, 0, 0, 0, 0);
        await PauseAsync(window);
        mouse_event(LeftUp, 0, 0, 0, 0);
        await PauseAsync(window);
        await SettleAsync(window);
    }

    /// <summary>Injects a real press, move and release.</summary>
    /// <param name="window">The shown window.</param>
    /// <param name="from">Start in window coordinates.</param>
    /// <param name="to">End in window coordinates.</param>
    /// <returns>A task that completes when the drag was processed.</returns>
    public static async Task DragAsync(Window window, double[] from, double[] to)
    {
        await MoveAsync(window, from[0], from[1]);
        mouse_event(LeftDown, 0, 0, 0, 0);
        await PauseAsync(window);
        const int Steps = 8;
        for (int i = 1; i <= Steps; i++)
        {
            await MoveAsync(window, from[0] + ((to[0] - from[0]) * i / Steps), from[1] + ((to[1] - from[1]) * i / Steps));
        }

        mouse_event(LeftUp, 0, 0, 0, 0);
        await PauseAsync(window);
        await SettleAsync(window);
    }

    private static async Task MoveAsync(Window window, double x, double y)
    {
        Point screen = window.PointToScreen(new Point(x, y));
        _ = SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        await PauseAsync(window);
    }

    // The input thread of Windows and Wine delivers the injected event as a message; one short idle round trip lets WPF handle it.
    private static async Task PauseAsync(Window window)
    {
        await Task.Delay(25);
        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Input);
    }

    private static UiAction CreateAction(string id, ScenarioStep step)
    {
        string kind = step.Kind ?? (step.Args is null ? (step.Value is null ? "click" : "toggle") : "submit");
        return kind switch
        {
            "toggle" => UiAction.Toggle(id, !string.Equals(step.Value, "false", StringComparison.OrdinalIgnoreCase)),
            "submit" => UiAction.Submit(id, (step.Args ?? []).ToDictionary(p => p.Key, p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())),
            _ => step.Args is null
                ? UiAction.Click(id)
                : new UiAction(id, UiActionKind.Click, null, step.Args.ToDictionary(p => p.Key, p => p.Value.GetString() ?? p.Value.GetRawText())),
        };
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", EntryPoint = "mouse_event")]
    private static partial void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extraInfo);
}
