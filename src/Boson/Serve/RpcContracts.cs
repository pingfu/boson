namespace Boson.Serve;

/// <summary>Wire types for the CLI↔daemon RPC over the unix socket.</summary>
public sealed record AddRequest(string Repo);

public sealed record AddStartResponse(string SetupUrl, string Token, string[] Warnings);

/// <summary>
/// Warnings arrive here rather than with the setup URL: what a project serves
/// is in `_boson.yml`, which boson cannot read until the App exists and the
/// clone has happened, which is the far end of this flow.
/// </summary>
public sealed record AddStatusResponse(
    string Phase, long? DeployId, string? Error, string[] Warnings, string[] EnvSets);

public sealed record DeployStartResponse(long DeployId);

public sealed record RemoveResponse(string AppSettingsUrl, bool Purged);

public sealed record ErrorResponse(string Error);
