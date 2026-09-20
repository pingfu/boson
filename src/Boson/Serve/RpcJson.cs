using System.Text.Json.Serialization;

namespace Boson.Serve;

/// <summary>What /_boson/health answers, and what `boson status` reads from it.</summary>
public sealed record HealthResponse(string Status, string Version, DateTimeOffset StartedAt);

/// <summary>
/// The webhook's answer to GitHub. The status code carries the meaning; this
/// is what makes a delivery readable in the Recent Deliveries panel.
/// </summary>
public sealed record WebhookAck(string Status);

/// <summary>
/// Source-generated so every message between the CLI, the daemon and GitHub
/// survives trimming. Reflection-based serialisation of these records is
/// invisible to the trimmer, and what it produces when their properties are
/// trimmed away is an empty object at runtime rather than a build error.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AddRequest))]
[JsonSerializable(typeof(AddStartResponse))]
[JsonSerializable(typeof(AddStatusResponse))]
[JsonSerializable(typeof(DeployStartResponse))]
[JsonSerializable(typeof(RemoveResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(WebhookAck))]
public sealed partial class RpcJson : JsonSerializerContext;
