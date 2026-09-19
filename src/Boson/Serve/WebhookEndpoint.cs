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
/// the deploy succeeded — outcomes live in `boson list` and the deploy log.
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
        ProjectLocks locks,
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
                return Results.Json(new { status = "pong" });
            }

            // 5 — body parses as a push event with a ref. Parsed only after the
            // signature verified attacker-controllable input.
            string? pushRef = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("ref", out var refProp)
                    && refProp.ValueKind == JsonValueKind.String)
                    pushRef = refProp.GetString();
            }
            catch (JsonException)
            {
            }
            if (pushRef is null)
            {
                Log(logger, repo, 400, "not a push event with a ref");
                return Results.BadRequest();
            }

            // 6 — branch filter.
            if (pushRef != $"refs/heads/{project.Branch}")
            {
                Log(logger, repo, 200, $"ignored ref {pushRef}");
                return Results.Json(new { status = "ignored" });
            }

            // 7 — activation filter.
            if (!project.WebhookActive)
            {
                if (locks.IsHeld(repo))
                {
                    projects.SetDeployPending(repo, true);
                    Log(logger, repo, 202, "inactive but first deploy in flight; coalesced");
                    return Results.Json(new { status = "queued" }, statusCode: StatusCodes.Status202Accepted);
                }
                Log(logger, repo, 200, "inactive");
                return Results.Json(new { status = "inactive" });
            }

            // All pass — 202, then the deploy on a background task.
            _ = Task.Run(async () =>
            {
                try
                {
                    await deployer.DeployAsync(repo, DeployTrigger.Webhook);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "{Repo}: webhook deploy task crashed", repo);
                }
            });
            Log(logger, repo, 202, "deploy queued");
            return Results.Json(new { status = "queued" }, statusCode: StatusCodes.Status202Accepted);
        });
    }

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
