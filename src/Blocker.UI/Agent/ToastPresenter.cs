using System.Windows.Threading;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Agent;

/// <summary>Shows agent toasts on the UI thread.</summary>
internal interface IToastPresenter
{
    /// <summary>Shows a toast; a newer toast replaces the one on screen.</summary>
    /// <param name="toast">Toast content and clock.</param>
    void Show(ToastViewModel toast);
}

/// <summary>Shows one <see cref="ToastWindow"/> at a time and closes it when its view model expires.</summary>
internal sealed class ToastPresenter : IToastPresenter
{
    private ToastWindow? _window;
    private DispatcherTimer? _timer;

    /// <inheritdoc />
    public void Show(ToastViewModel toast)
    {
        Close();
        ToastWindow window = new(toast);
        _window = window;
        window.Show();
        window.PlaceBottomRight();

        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            toast.Refresh();
            if (toast.IsExpired)
            {
                Close();
            }
        };
        _timer = timer;
        timer.Start();
    }

    private void Close()
    {
        _timer?.Stop();
        _timer = null;
        _window?.Close();
        _window = null;
    }
}
