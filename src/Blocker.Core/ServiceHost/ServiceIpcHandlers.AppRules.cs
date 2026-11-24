using Blocker.Contracts;
using Blocker.Core.AppRules;
using Blocker.Core.Ipc;

namespace Blocker.Core.ServiceHost;

// Saved app sets (plain storage) and per-app rules (enforced outside sessions by AppRuleEnforcer).
internal sealed partial class ServiceIpcHandlers
{
    private async ValueTask<AppSetsDto> GetAppSetsAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken) =>
        await deps.AppSets.LoadAsync(cancellationToken).ConfigureAwait(false);

    private async ValueTask<OperationResponse> SetAppSetsAsync(IpcRequestContext context, AppSetsDto request, CancellationToken cancellationToken)
    {
        Require(request.Sets);
        AppSetsValidation validation = AppSetValidator.Validate(request);
        if (validation.Sets is not { } sets)
        {
            return new OperationResponse(false, validation.Errors);
        }

        await deps.AppSets.SaveAsync(sets, cancellationToken).ConfigureAwait(false);
        return new OperationResponse(true, []);
    }

    private ValueTask<AppRulesDto> GetAppRulesAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(deps.AppRules.Rules);

    private async ValueTask<OperationResponse> SetAppRulesAsync(IpcRequestContext context, AppRulesDto request, CancellationToken cancellationToken)
    {
        Require(request.Rules);
        RequireNoSession();
        IReadOnlyList<string> errors = await deps.AppRules.SetAsync(request, cancellationToken).ConfigureAwait(false);
        return new OperationResponse(errors.Count == 0, errors);
    }

    private ValueTask<AppRulesStatusResponse> GetAppRulesStatusAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(deps.AppRules.GetStatus(deps.Coordinator.IsSessionActive));
}
