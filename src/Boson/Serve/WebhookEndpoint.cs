using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Boson.Deploy;
using Boson.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Boson.Serve;

/// <summary>
/// POST /_boson/webhook/{org}/{name} — the validation pipeline, in order, each
/// step short-circuiting. The response is sent before any deploy work starts,
/// so GitHub's 10-second budget is met by construction.
///
/// The status codes carry exactly two meanings, which is what makes GitHub's
/// Recent Deliveries page readable at a glance: 403 means the signature did not
/// verify (secret drift, an incident); 200 and 202 both mean it did, 200 when
/// boson declines the push and 202 when a deploy was queued. A 202 never means
/// the deploy succeeded — outcomes live in `boson status` and the deploy log.
/// </summary>
public static class WebhookEndpoint
{
    /// <summary>
    /// GitHub's own webhook payload cap, so no legitimate delivery is ever
    /// rejected for size.
    /// </summary>
    public const long MaxBodyBytes = 25 * 1024 * 1024;

    public static void Map(
        IEndpointRouteBuilder app,
        IProjectsRepository projects,
        IDeploymentsRepository deployments,
        DeploymentLocks locks,
        IDeployer deployer,
        ILogger logger)
    {
        app.MapPost("/_boson/webhook/{org}/{name}", async (HttpContext ctx, string org, string name) =>
        {
            var repo = $"{org}/{name}".ToLowerInvariant();

            // 1 — buffer the whole body; the HMAC needs the raw bytes.
            var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBodyBytes)
                {
                    Log(logger, repo, 413, "body over 25 MB");
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
            }
            var raw = buffer.ToArray();

            // 2 — project row exists and is not archived.
            var project = projects.GetByRepo(repo);
            if (project is null)
            {
                Log(logger, repo, 404, "unknown project");
                return Results.NotFound();
            }

            // 3 — HMAC over the raw bytes, constant-time compare. 403 = secret drift.
            if (!SignatureValid(ctx.Request.Headers["X-Hub-Signature-256"].ToString(),
                    raw, project.GithubWebhookSecret))
            {
                Log(logger, repo, 403, "signature verification failed");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // 4 — GitHub sends one ping at App creation; handled, not an error.
            if (ctx.Request.Headers["X-GitHub-Event"].ToString() == "ping")
            {
                Log(logger, repo, 200, "ping");
                return Ack("pong");
            }

            // 5 — body parses as a push event with a ref. Parsed only after the
            // signature verified attacker-controllable input.
            string? gitRef = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("ref", out var refProp)
                    && refProp.ValueKind == JsonValueKind.String)
                    gitRef = refProp.GetString();
            }
            catch (JsonException)
            {
            }
            if (gitRef is null)
            {
                Log(logger, repo, 400, "no ref in the payload");
                return Results.BadRequest();
            }

            const string branchPrefix = "refs/heads/";

            if (!gitRef.StartsWith(branchPrefix, StringComparison.Ordinal))
            {
                Log(logger, repo, 200, $"ignored ref {gitRef}");
                return Ack("ignored");
            }

            var branch = gitRef[branchPrefix.Length..];

            // 6 — activation filter, per branch. A branch boson has not seen
            // deploys on its first push: `_boson.yml` is what decides, and only
            // the fetch can read it.
            var deployment = deployments.GetByBranch(repo, branch);

            if (deployment is { WebhookActive: false })
            {
                if (locks.IsHeld(Deployer.LockKey(repo, branch)))
                {
                    deployments.SetDeployPending(deployment.Id, true);
                    Log(logger, repo, 202, $"{branch} inactive but first deploy in flight; coalesced");
                    return Ack("queued", StatusCodes.Status202Accepted);
                }

                Log(logger, repo, 200, $"{branch} inactive");
                return Ack("inactive");
            }

            // All pass — 202, then the deploy on a background task.
            _ = Task.Run(async () =>
            {
                try
                {
                    await deployer.DeployAsync(repo, branch, DeployTrigger.Webhook);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "{Repo}#{Branch}: webhook deploy task crashed", repo, branch);
                }
            });
            Log(logger, repo, 202, $"deploy queued for {branch}");
            return Ack("queued", StatusCodes.Status202Accepted);
        });
    }

    private static IResult Ack(string status, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(new WebhookAck(status), RpcJson.Default.WebhookAck, statusCode: statusCode);

    private static bool SignatureValid(string header, byte[] body, string secret)
    {
        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(header[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }
        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(computed, provided);
    }

    private static void Log(ILogger logger, string repo, int status, string reason) =>
        logger.LogInformation("webhook {Repo}: {Status} {Reason}", repo, status, reason);
}
