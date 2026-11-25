using Blocker.Contracts;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Tests;

public sealed class SettingsViewModelTests
{
    private readonly FakeServiceConnection _connection = new();
    private readonly FakeSnackbarService _snackbar = new();
    private readonly List<IReadOnlyDictionary<string, string>> _saved = [];
    private string _stored = "off";
    private bool _acceptSave = true;

    private SettingsViewModel Create()
    {
        _connection.On<SettingsDto>(IpcMethods.SettingsGet, () => new SettingsDto(new Dictionary<string, string> { ["blockNotice.show"] = _stored }));
        _connection.On<SettingsDto, OperationResponse>(IpcMethods.SettingsSet, settings =>
        {
            if (!_acceptSave)
            {
                return new OperationResponse(false, ["Refused."]);
            }

            _saved.Add(settings.Values);
            return new OperationResponse(true, []);
        });
        return new SettingsViewModel(_connection, _snackbar);
    }

    [Fact]
    public void The_block_notice_switch_shows_the_stored_value_without_saving_it_back()
    {
        SettingsViewModel vm = Create();

        Assert.False(vm.ShowBlockNotice);
        Assert.Empty(_saved);
    }

    [Fact]
    public void Flipping_the_block_notice_switch_saves_only_that_setting()
    {
        SettingsViewModel vm = Create();

        vm.ShowBlockNotice = true;

        IReadOnlyDictionary<string, string> saved = Assert.Single(_saved);
        Assert.Equal(new Dictionary<string, string> { ["blockNotice.show"] = "on" }, saved);
    }

    [Fact]
    public void A_refused_save_puts_the_switch_back()
    {
        _acceptSave = false;
        SettingsViewModel vm = Create();

        vm.ShowBlockNotice = true;

        Assert.False(vm.ShowBlockNotice);
    }

    [Fact]
    public void The_switch_is_locked_while_a_session_runs()
    {
        SettingsViewModel vm = Create();
        Assert.True(vm.CanChangeBlockNotice);

        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));

        Assert.False(vm.CanChangeBlockNotice);
    }
}
