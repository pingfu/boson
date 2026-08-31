namespace Boson.Storage;

public sealed class Project
{
    public long Id { get; set; }
    public string Repo { get; set; } = "";
    public string Hostname { get; set; } = "";
    public int UpstreamPort { get; set; }
    public string Branch { get; set; } = "main";
    public long GithubAppId { get; set; }
    public string GithubAppSlug { get; set; } = "";
    public long GithubInstallationId { get; set; }
    public string GithubWebhookSecret { get; set; } = "";
    public string GithubAppPem { get; set; } = "";
    public bool WebhookActive { get; set; }
    public bool DeployPending { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public string? ArchivedAt { get; set; }

    public string AppSettingsUrl => $"https://github.com/settings/apps/{GithubAppSlug}";
}

public sealed class DeployRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string Trigger { get; set; } = "";       // 'manual' | 'webhook'
    public string? CommitSha { get; set; }
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    public string Status { get; set; } = "running"; // 'running' | 'succeeded' | 'failed'
    public string LogPath { get; set; } = "";
    public string? Error { get; set; }
}

public enum DeployTrigger { Manual, Webhook }

public static class DeployTriggerExtensions
{
    public static string AsDbValue(this DeployTrigger t) =>
        t == DeployTrigger.Manual ? "manual" : "webhook";
}
