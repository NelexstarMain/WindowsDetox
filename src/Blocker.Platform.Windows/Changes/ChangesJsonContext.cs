using System.Text.Json.Serialization;

namespace Blocker.Platform.Windows.Changes;

/// <summary>Source-generated JSON metadata for every journaled state record (compact, camelCase, nulls omitted).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(IfeoOriginalState))]
[JsonSerializable(typeof(IfeoDesiredState))]
[JsonSerializable(typeof(RegistryPolicyOriginalState))]
[JsonSerializable(typeof(PolicyValue))]
[JsonSerializable(typeof(ServiceDaclOriginalState))]
[JsonSerializable(typeof(ServiceDaclDesiredState))]
[JsonSerializable(typeof(NtfsDenyOriginalState))]
[JsonSerializable(typeof(NtfsDenyDesiredState))]
internal sealed partial class ChangesJsonContext : JsonSerializerContext;
