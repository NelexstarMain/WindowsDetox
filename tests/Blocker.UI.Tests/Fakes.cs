using Blocker.Contracts;
using Blocker.Sdk.Session;
using Blocker.UI.Controls.Services;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;
using Wpf.Ui.Controls;

namespace Blocker.UI.Tests;

/// <summary>Scriptable stand-in for the service connection: handlers per method, recorded calls, manual pushes.</summary>
internal sealed class FakeServiceConnection : IServiceConnection
{
    private readonly Dictionary<string, Func<object?, object>> _handlers = new(StringComparer.Ordinal);

    public FakeServiceConnection()
    {
        On<GetContributionsRequest, GetContributionsResponse>(IpcMethods.UiGetContributions, r => new GetContributionsResponse(r.Slot, []));
        On<ListPluginsResponse>(IpcMethods.PluginsList, () => new ListPluginsResponse([]));
    }

    public event EventHandler? ConnectionChanged;

    public event EventHandler? SessionChanged;

    public event EventHandler<NotificationEventArgs>? NotificationReceived;

    public bool IsConnected { get; private set; } = true;

    public SessionSnapshot Session { get; private set; } = SessionSnapshot.Idle;

    public List<(string Method, object? Request)> Calls { get; } = [];

    public void On<TRequest, TResponse>(string method, Func<TRequest, TResponse> handler)
        where TResponse : notnull =>
        _handlers[method] = request => handler((TRequest)request!);

    public void On<TResponse>(string method, Func<TResponse> handler)
        where TResponse : notnull =>
        _handlers[method] = _ => handler();

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Push(SessionSnapshot snapshot)
    {
        Session = snapshot;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Notify(NotificationDto notification) => NotificationReceived?.Invoke(this, new NotificationEventArgs(notification));

    public int CallCount(string method) => Calls.Count(c => c.Method == method);

    public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken)
    {
        Calls.Add((method, request));
        return ValueTask.FromResult((TResponse)Resolve(method, request));
    }

    public ValueTask<TResponse> InvokeAsync<TResponse>(string method, CancellationToken cancellationToken)
    {
        Calls.Add((method, null));
        return ValueTask.FromResult((TResponse)Resolve(method, null));
    }

    private object Resolve(string method, object? request) =>
        _handlers.TryGetValue(method, out Func<object?, object>? handler)
            ? handler(request)
            : throw new IpcException(IpcErrorCode.NotFound, $"No fake for {method}.");
}

internal sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public List<(string Title, string Message, string Confirm)> Confirmations { get; } = [];

    public void SetHost(ContentDialogHost host)
    {
    }

    public Task ShowMessageAsync(string title, string message, string closeText = "Close", CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "Cancel", CancellationToken cancellationToken = default)
    {
        Confirmations.Add((title, message, confirmText));
        return Task.FromResult(ConfirmResult);
    }
}

internal sealed class FakeSnackbarService : Blocker.UI.Controls.Services.ISnackbarService
{
    public List<(string Title, string Message, NotificationSeverity Severity)> Shown { get; } = [];

    public void SetPresenter(SnackbarPresenter presenter)
    {
    }

    public void Show(string title, string message, NotificationSeverity severity = NotificationSeverity.Info, TimeSpan? timeout = null) =>
        Shown.Add((title, message, severity));
}

internal sealed class FakeAppDialogs : IAppDialogs
{
    public string? CodeToEnter { get; set; }

    public List<string> ShownCodes { get; } = [];

    public List<PluginInstallReport> Reports { get; } = [];

    public Task ShowBreakGlassCodeAsync(string code, CancellationToken cancellationToken)
    {
        ShownCodes.Add(code);
        return Task.CompletedTask;
    }

    public Task<string?> PromptBreakGlassCodeAsync(CancellationToken cancellationToken) => Task.FromResult(CodeToEnter);

    public Task ShowInstallReportAsync(PluginInstallReport report, CancellationToken cancellationToken)
    {
        Reports.Add(report);
        return Task.CompletedTask;
    }
}

internal sealed class FakeFileDialogs : IFileDialogs
{
    public string? SavePath { get; set; }

    public string? OpenPath { get; set; }

    public string? PickSavePath(string suggestedFileName, string filter) => SavePath;

    public string? PickOpenPath(string filter) => OpenPath;
}

internal sealed class FakeTicker : IUiTicker
{
    public event EventHandler? Tick;

    public void Raise() => Tick?.Invoke(this, EventArgs.Empty);
}

internal static class Snapshots
{
    public static SessionSnapshot Active(TimeSpan remaining, int passesLeft = 2, int passesPerDay = 2) =>
        new(SessionPhase.Active, Guid.NewGuid(), remaining, null, passesLeft, passesPerDay, TimeSpan.FromMinutes(10));

    public static SessionSnapshot Pass(TimeSpan remaining, TimeSpan passRemaining, int passesLeft = 1) =>
        new(SessionPhase.EmergencyPass, Guid.NewGuid(), remaining, passRemaining, passesLeft, 2, TimeSpan.FromMinutes(10));

    public static SessionStatusDto Dto(SessionSnapshot snapshot) => SessionStatusDto.From(snapshot);
}
