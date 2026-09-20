using Boson.Storage;
using Boson.Util;
using Microsoft.Extensions.Logging;

namespace Boson.Deploy;

/// <summary>
/// Stops deployments nothing has pushed to for as long as their `expire_after`
/// allows. A branch matched by a pattern is created by a push and forgotten by
/// the person who pushed it, so without this the host fills with sites nobody
/// is looking at. Deleting the branch is the other way they end, and GitHub
/// does not redeliver the event that says so.
/// </summary>
public sealed class ExpiryReaper(
    IDeploymentsRepository deployments,
    IDeploysRepository deploys,
    IDeployer deployer,
    TimeProvider clock,
    ILogger logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, clock, ct);
                await SweepAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // A sweep that throws must not take the daemon's only reaper
                // with it; the next one is fifteen minutes away.
                logger.LogError(e, "expiry sweep failed");
            }
        }
    }

    public async Task SweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        foreach (var deployment in deployments.ListActive())
        {
            if (ParseDuration(deployment.ExpireAfter) is not { } window) continue;

            // Measured from the last deploy that worked: a deployment whose
            // recent deploys all failed is still one somebody is using.
            var last = deploys.GetLastSucceededForDeployment(deployment.Id);
            var since = DbTime.Parse(last?.FinishedAt ?? last?.StartedAt)
                        ?? DbTime.Parse(deployment.CreatedAt);

            if (since is null || now - since.Value < window) continue;

            logger.LogInformation(
                "{Repo}#{Branch}: idle for {Window}; stopping it",
                deployment.Repo, deployment.Branch, deployment.ExpireAfter);

            await deployer.TearDownAsync(deployment, ct);
        }
    }

    /// <summary>`14d`, `36h`, `90m`. Anything else is ignored rather than guessed at.</summary>
    internal static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim();
        var unit = value[^1];
        var digits = value[..^1];

        if (!int.TryParse(digits, out var amount) || amount <= 0) return null;

        return unit switch
        {
            'd' or 'D' => TimeSpan.FromDays(amount),
            'h' or 'H' => TimeSpan.FromHours(amount),
            'm' or 'M' => TimeSpan.FromMinutes(amount),
            _ => null,
        };
    }
}
