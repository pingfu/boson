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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A sweep that throws must not take the daemon's only reaper
                // with it; the next one is fifteen minutes away. That includes
                // a timeout, which arrives as a cancellation nobody asked for.
                logger.LogError(e, "expiry sweep failed");
            }
        }
    }

    public async Task SweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        foreach (var deployment in deployments.ListActive())
        {
            try
            {
                await SweepOneAsync(deployment, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // One deployment that cannot be swept must not shield every
                // deployment listed after it from expiring.
                logger.LogError(e, "{Repo}#{Branch}: expiry sweep failed", deployment.Repo, deployment.Branch);
            }
        }
    }

    private async Task SweepOneAsync(Deployment deployment, DateTimeOffset now, CancellationToken ct)
    {
        if (ParseDuration(deployment.ExpireAfter) is not { } window) return;

        // Measured from the last deploy that worked: failed deploys do not
        // keep a deployment alive.
        var last = deploys.GetLastSucceededForDeployment(deployment.Id);
        var since = DbTime.Parse(last?.FinishedAt ?? last?.StartedAt)
                    ?? DbTime.Parse(deployment.CreatedAt);

        if (since is null || now - since.Value < window) return;

        logger.LogInformation(
            "{Repo}#{Branch}: idle for {Window}; stopping it",
            deployment.Repo, deployment.Branch, deployment.ExpireAfter);

        await deployer.TearDownAsync(deployment, ct);
    }

    /// <summary>
    /// `14d`, `36h`, `90m`. Null for anything else, including a number too
    /// large to be a time span, which `_boson.yml` validation turns into an
    /// error naming the value.
    /// </summary>
    internal static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim();
        var unit = value[^1];
        var digits = value[..^1];

        // Digits alone: int.TryParse would also take a sign and whitespace.
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return null;
        if (!int.TryParse(digits, out var amount) || amount <= 0) return null;

        try
        {
            return unit switch
            {
                'd' or 'D' => TimeSpan.FromDays(amount),
                'h' or 'H' => TimeSpan.FromHours(amount),
                'm' or 'M' => TimeSpan.FromMinutes(amount),
                _ => null,
            };
        }
        catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
