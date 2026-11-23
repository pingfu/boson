using Boson.Deploy;
using Boson.Github;
using Boson.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Boson.Serve;

/// <summary>
/// POST /deploy/{org}/{name} on the CLI socket. Every refusal carries the
/// reason in an <see cref="ErrorResponse"/>, which `boson deploy` prints as
/// it stands: one status covers several reasons, and only this side knows
/// which one it was. A deploy answers with its id once the fetch has read
/// what the branch declares, and a declaration answers that it has begun.
/// </summary>
public static class DeployEndpoint
{
    public static void Map(
        IEndpointRouteBuilder app,
        IProjectsRepository projects,
        IDeploymentsRepository deployments,
        IDeployer deployer,
        ManifestFlowOrchestrator orchestrator,
        ILogger log)
    {
        // In-memory, so a 404 here is a daemon that restarted mid-declaration.
        app.MapGet("/declare/status/{org}/{name}", (string org, string name) =>
        {
            var status = orchestrator.GetDeclareStatus($"{org}/{name}".ToLowerInvariant());

            return status is null
                ? Results.NotFound()
                : Results.Json(status, RpcJson.Default.DeclareStatusResponse);
        });

        app.MapPost("/deploy/{org}/{name}", async (HttpContext ctx, string org, string name) =>
        {
            var repo = $"{org}/{name}".ToLowerInvariant();
            var requested = ctx.Request.Query["branch"].ToString();

            var active = deployments.ListActiveForRepo(repo);

            // A declaration makes its deployments one at a time and withdraws
            // them all if a later one fails. Until it ends the project is not
            // declared, however many rows exist, and a deploy started now
            // would build a deployment that may be archived underneath it.
            if (orchestrator.IsDeclaring(repo))
                return Results.Json(new DeployStartResponse(0, Declaring: true),
                    RpcJson.Default.DeployStartResponse, statusCode: StatusCodes.Status202Accepted);

            if (active.Count == 0)
            {
                var project = projects.GetByRepo(repo);

                if (project is null)
                    return Results.NotFound(new ErrorResponse($"unknown project: {repo}"));

                // A project with no deployments is one whose `boson add` saved
                // the App and then could not fetch, or could not read
                // `_boson.yml`. `boson add` refuses a repo it already holds, so
                // this is the only place that state can be completed from.
                //
                // Started and not awaited: a first clone has no upper bound a
                // request should be held open for. The CLI follows the status
                // below and asks again once the deployments exist.
                orchestrator.BeginDeclare(project);

                return Results.Json(new DeployStartResponse(0, Declaring: true),
                    RpcJson.Default.DeployStartResponse, statusCode: StatusCodes.Status202Accepted);
            }

            // Deploying every branch on a bare `boson deploy <repo>` would
            // rebuild sites nobody asked about, so more than one means naming
            // the one you meant.
            if (string.IsNullOrWhiteSpace(requested) && active.Count > 1)
                return Results.Conflict(new ErrorResponse(
                    $"{repo} deploys {string.Join(", ", active.Select(d => d.Branch))}; " +
                    "name one with --branch"));

            var targets = string.IsNullOrWhiteSpace(requested)
                ? active
                : [.. active.Where(d => d.Branch == requested)];

            if (targets.Count == 0)
                return Results.NotFound(new ErrorResponse($"{repo} has no deployment for {requested}"));

            // Race the deploy id against the deploy itself. The CLI needs the id
            // to start tailing the log, and it arrives long before the deploy
            // ends. The outcomes that never reach a deploy row (unknown
            // project, lock already held) never raise onStarted at all, so
            // waiting only on the id would hang the CLI on exactly those.
            var started = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

            var deployTask = Task.Run(async () =>
            {
                DeployResult last = new DeployResult.NotFound();

                foreach (var target in targets)
                    last = await deployer.DeployAsync(
                        repo, target.Branch, DeployTrigger.Manual, id => started.TrySetResult(id));

                return last;
            });

            _ = deployTask.ContinueWith(
                t => log.LogError(t.Exception, "{Repo}: manual deploy task crashed", repo),
                TaskContinuationOptions.OnlyOnFaulted);

            var winner = await Task.WhenAny(started.Task, deployTask);

            if (winner == started.Task)
                return Results.Json(new DeployStartResponse(await started.Task),
                    RpcJson.Default.DeployStartResponse, statusCode: StatusCodes.Status202Accepted);

            return await deployTask switch
            {
                // No deploy row was written, so there is no id to follow and
                // the reason has to travel in the answer. A 202 here would
                // leave the CLI waiting on a deploy that does not exist.
                DeployResult.Stopped s => Results.UnprocessableEntity(new ErrorResponse(s.Error)),
                DeployResult.Completed { LastDeployId: 0 } => Results.UnprocessableEntity(new ErrorResponse(
                    $"the deploy of {repo} ended before it started; journalctl -u boson has the reason")),
                DeployResult.Completed c => Results.Json(new DeployStartResponse(c.LastDeployId),
                    RpcJson.Default.DeployStartResponse, statusCode: StatusCodes.Status202Accepted),
                DeployResult.LockHeld => Results.Conflict(new ErrorResponse(
                    $"a deploy for {repo} is already running; re-run when it finishes (boson status shows it)")),
                DeployResult.NotFound => Results.NotFound(new ErrorResponse($"unknown project: {repo}")),
                DeployResult.NotDeclared => Results.Conflict(new ErrorResponse(
                    $"{BosonFile.FileName} declares no deployment for this branch")),
                _ => Results.Conflict(new ErrorResponse("deploy coalesced")),
            };
        });
    }
}
