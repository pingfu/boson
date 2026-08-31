namespace Boson.Storage;

/// <summary>A snapshot of a projects row; updates go through SQL, never mutation.</summary>
public sealed record Project
{
    public long Id { get; init; }
    public required string Repo { get; init; }
    public required string Hostname { get; init; }
    public required int UpstreamPort { get; init; }
    public string Branch { get; init; } = "main";
    public required long GithubAppId { get; init; }
    public required string GithubAppSlug { get; init; }
    public required long GithubInstallationId { get; init; }
    public required string GithubWebhookSecret { get; init; }
    public required string GithubAppPem { get; init; }
    public bool WebhookActive { get; init; }
    public bool DeployPending { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public string? ArchivedAt { get; init; }

    public string AppSettingsUrl => $"https://github.com/settings/apps/{GithubAppSlug}";
}

/// <summary>A snapshot of a deploys row; only Dapper constructs it.</summary>
public sealed record DeployRow
{
    public long Id { get; init; }
    public long ProjectId { get; init; }
    public DeployTrigger Trigger { get; init; }
    public string? CommitSha { get; init; }
    public string? StartedAt { get; init; }
    public string? FinishedAt { get; init; }
    public DeployStatus Status { get; init; } = DeployStatus.Running;
    public string LogPath { get; init; } = "";
    public string? Error { get; init; }
}

public enum DeployTrigger { Manual, Webhook }

public enum DeployStatus { Running, Succeeded, Failed }

/// <summary>
/// The database stores lowercase strings (operators read boson.db directly);
/// these are the boundary conversions. Dapper reads the columns back via
/// case-insensitive Enum.Parse.
/// </summary>
public static class DeployEnumExtensions
{
    public static string AsDbValue(this DeployTrigger t) =>
        t == DeployTrigger.Manual ? "manual" : "webhook";

    public static string AsDbValue(this DeployStatus s) => s switch
    {
        DeployStatus.Running => "running",
        DeployStatus.Succeeded => "succeeded",
        _ => "failed",
    };
}
