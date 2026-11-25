using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Blocker.UI.Sdk;

namespace Blocker.Contracts;

/// <summary>Strict, source-generated serializer for all IPC messages.</summary>
public static class IpcJson
{
    /// <summary>Gets the serializer options (strict: unknown members are errors).</summary>
    public static JsonSerializerOptions Options => IpcJsonContext.Default.Options;

    /// <summary>Gets the source-generated metadata for <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A type registered in <see cref="IpcJsonContext"/>.</typeparam>
    /// <returns>The metadata.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not part of the contract.</exception>
    public static JsonTypeInfo<T> TypeInfo<T>() =>
        IpcJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"{typeof(T).Name} is not registered in the IPC contract.");

    /// <summary>Serializes a value to a detached <see cref="JsonElement"/>.</summary>
    /// <typeparam name="T">Contract type.</typeparam>
    /// <param name="value">Value.</param>
    /// <returns>The element.</returns>
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());

    /// <summary>Deserializes <paramref name="element"/> strictly.</summary>
    /// <typeparam name="T">Contract type.</typeparam>
    /// <param name="element">JSON value.</param>
    /// <returns>The value.</returns>
    /// <exception cref="JsonException">The JSON does not match the contract (including unknown members or <c>null</c>).</exception>
    public static T FromElement<T>(JsonElement element) =>
        element.Deserialize(TypeInfo<T>()) ?? throw new JsonException("Value must not be null.");
}

/// <summary>Source-generated metadata for the IPC contract. Strict and bounded: unknown members are rejected, depth is limited.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    MaxDepth = 32,
    RespectNullableAnnotations = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcEnvelope))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(EmptyDto))]
[JsonSerializable(typeof(OperationResponse))]
[JsonSerializable(typeof(SessionStatusDto))]
[JsonSerializable(typeof(StartSessionRequest))]
[JsonSerializable(typeof(StartSessionResponse))]
[JsonSerializable(typeof(EmergencyPassResponse))]
[JsonSerializable(typeof(BreakGlassRequest))]
[JsonSerializable(typeof(BreakGlassResponse))]
[JsonSerializable(typeof(DevAbortResponse))]
[JsonSerializable(typeof(AllowlistDto))]
[JsonSerializable(typeof(AppSetsDto))]
[JsonSerializable(typeof(AppRulesDto))]
[JsonSerializable(typeof(AppRulesStatusResponse))]
[JsonSerializable(typeof(ScanAppsResponse))]
[JsonSerializable(typeof(SettingsDto))]
[JsonSerializable(typeof(ListPluginsResponse))]
[JsonSerializable(typeof(InstallPluginRequest))]
[JsonSerializable(typeof(InstallPluginResponse))]
[JsonSerializable(typeof(RemovePluginRequest))]
[JsonSerializable(typeof(SetPluginEnabledRequest))]
[JsonSerializable(typeof(DownloadTemplateRequest))]
[JsonSerializable(typeof(DownloadTemplateResponse))]
[JsonSerializable(typeof(GetContributionsRequest))]
[JsonSerializable(typeof(GetContributionsResponse))]
[JsonSerializable(typeof(SendActionRequest))]
[JsonSerializable(typeof(UiAction))]
[JsonSerializable(typeof(AgentRegisterRequest))]
[JsonSerializable(typeof(AgentRegisterResponse))]
[JsonSerializable(typeof(ForegroundWindowDto))]
[JsonSerializable(typeof(IdleChangedDto))]
[JsonSerializable(typeof(CloseProcessRequest))]
[JsonSerializable(typeof(CloseProcessResponse))]
[JsonSerializable(typeof(NotificationDto))]
[JsonSerializable(typeof(BlockInfoRequest))]
[JsonSerializable(typeof(BlockInfoResponse))]
[JsonSerializable(typeof(PluginHostRegisterRequest))]
[JsonSerializable(typeof(PluginHostRegisterResponse))]
[JsonSerializable(typeof(PluginFaultedNotification))]
[JsonSerializable(typeof(RenderRequestedNotification))]
[JsonSerializable(typeof(LoadPluginsRequest))]
[JsonSerializable(typeof(LoadPluginsResponse))]
[JsonSerializable(typeof(EvaluatePackageRequest))]
[JsonSerializable(typeof(PluginInstallReportDto))]
[JsonSerializable(typeof(InterceptProcessRequest))]
[JsonSerializable(typeof(InterceptWindowRequest))]
[JsonSerializable(typeof(InterceptResponse))]
[JsonSerializable(typeof(EvaluateLockRequest))]
[JsonSerializable(typeof(EvaluateLockResponse))]
[JsonSerializable(typeof(InvokePluginMethodRequest))]
[JsonSerializable(typeof(InvokePluginMethodResponse))]
[JsonSerializable(typeof(SessionChangedRequest))]
[JsonSerializable(typeof(HostEventDto))]
[JsonSerializable(typeof(SubscribeResponse))]
[JsonSerializable(typeof(PluginStartSessionRequest))]
[JsonSerializable(typeof(PluginStartSessionResponse))]
[JsonSerializable(typeof(PluginNotifyRequest))]
[JsonSerializable(typeof(PluginNotifyResponse))]
public sealed partial class IpcJsonContext : JsonSerializerContext
{
}
