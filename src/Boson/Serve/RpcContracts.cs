namespace Boson.Serve;

// Wire types for the CLI↔daemon RPC over the unix socket.

/// <summary>
/// <paramref name="Owner"/> is "org" or "user" when the operator said which
/// kind of account owns the repository, and null for the daemon to ask GitHub.
/// </summary>
public sealed record AddRequest(string Repo, string? Owner = null);

public sealed record AddStartResponse(string SetupUrl, string Token, string[] Warnings);

/// <summary>
/// Warnings arrive here rather than with the setup URL: what a project serves
/// is in `_boson.yml`, which boson cannot read until the App exists and the
/// clone has happened, which is the far end of this flow.
/// </summary>
public sealed record AddStatusResponse(
    string Phase, long? DeployId, string? Error, string[] Warnings, string[] EnvSets);

/// <summary>
/// Where a declaration `boson deploy` started has got to. <paramref name="Phase"/>
/// is "declaring", "done" or "failed"; <paramref name="Error"/> is why it
/// failed, and the warnings are what it left for the operator to do.
/// </summary>
public sealed record DeclareStatusResponse(string Phase, string? Error, string[] Warnings);

/// <summary>
/// <paramref name="Declaring"/> means no deploy has started: the project had
/// no deployments, the daemon is making them, and the CLI follows that at
/// /declare/status before asking again.
/// </summary>
public sealed record DeployStartResponse(long DeployId, bool Declaring = false);

public sealed record RemoveResponse(string AppSettingsUrl, bool Purged);

public sealed record ErrorResponse(string Error);
