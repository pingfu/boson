namespace Boson.Serve;

/// <summary>Wire types for the CLI↔daemon RPC over the unix socket.</summary>
public sealed record AddRequest(string Repo, string Hostname, string Branch);

public sealed record AddStartResponse(string SetupUrl, string Token, string[] Warnings);

public sealed record AddStatusResponse(string Phase, long? DeployId, string? Error);

public sealed record DeployStartResponse(long DeployId);

public sealed record RemoveResponse(string AppSettingsUrl, bool Purged);

public sealed record ErrorResponse(string Error);
