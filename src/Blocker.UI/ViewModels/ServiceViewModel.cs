using CommunityToolkit.Mvvm.ComponentModel;
using Blocker.UI.Controls.Services;
using Blocker.UI.Services;

namespace Blocker.UI.ViewModels;

/// <summary>Base of view models that call the Blocker service and report expected failures as snackbars.</summary>
internal abstract class ServiceViewModel(IServiceConnection connection, ISnackbarService snackbar) : ObservableObject
{
    /// <summary>Gets the connection to the service.</summary>
    protected IServiceConnection Connection { get; } = connection;

    /// <summary>Gets the snackbar service.</summary>
    protected ISnackbarService Snackbar { get; } = snackbar;

    /// <summary>Runs a service call and shows a snackbar when it fails in an expected way (IPC error, unreachable service, timeout).</summary>
    /// <param name="action">The call.</param>
    /// <param name="failureTitle">Snackbar title on failure.</param>
    /// <returns><see langword="true"/> when the call completed.</returns>
    protected async Task<bool> TryAsync(Func<Task> action, string failureTitle)
    {
        try
        {
            await action().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ServiceFault.IsExpected(ex))
        {
            Snackbar.Show(failureTitle, ServiceFault.Describe(ex), NotificationSeverity.Error);
            return false;
        }
    }
}
