using System.Windows;
using System.Windows.Threading;

namespace Blocker.UI.Services;

/// <summary>Marshals work onto the UI thread.</summary>
internal interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread, inline when already there.</summary>
    /// <param name="action">Work to run.</param>
    void Post(Action action);
}

/// <summary>Dispatcher of the running WPF application.</summary>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary>A steady tick on the UI thread that drives live countdowns.</summary>
internal interface IUiTicker
{
    /// <summary>Raised about four times per second.</summary>
    event EventHandler? Tick;
}

/// <summary>Ticker backed by a <see cref="DispatcherTimer"/>; create it on the UI thread.</summary>
internal sealed class DispatcherUiTicker : IUiTicker
{
    private readonly DispatcherTimer _timer;

    /// <summary>Creates and starts the ticker.</summary>
    public DispatcherUiTicker()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
        _timer.Start();
    }

    /// <inheritdoc />
    public event EventHandler? Tick;
}
