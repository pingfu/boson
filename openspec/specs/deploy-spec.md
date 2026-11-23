# deploy Specification

## Purpose

A deploy fetches the tip of a branch, reads `_boson.yml`, and runs `docker compose up` for that branch's deployment. `boson deploy <org/name> [--branch <name>]` starts one by hand and streams its log. Pushes start them through [webhook-deploy](webhook-deploy-spec.md).

## Requirements

### Requirement: Choosing what to deploy
The system SHALL deploy the project's one active deployment, or the one `--branch` names.

#### Scenario: Unknown project
- WHEN no active project exists for the repository
- THEN the command prints `unknown project: <org/name>` and exits 1

#### Scenario: Several deployments, no branch named
- WHEN the project has more than one active deployment and `--branch` is absent
- THEN the command prints `<org/name> deploys <branch>, <branch>; name one with --branch` and exits 3

[bug] The `--branch` help text says `default: every branch this project deploys`. Exit 3 is the lock contention code, used here because the refusal is a 409.

#### Scenario: Branch has no deployment
- WHEN `--branch` names a branch with no active deployment
- THEN the command prints `<org/name> has no deployment for <branch>` and exits 1

### Requirement: Completing a project with no deployments
When a project has no active deployment, the system SHALL start the declaration specified in [project-add](project-add-spec.md) in the background and answer at once. The CLI SHALL poll its status once a second and request the deploy again when it is done.

#### Scenario: Declaration succeeds
- WHEN `boson deploy` is run for a project whose add stopped before declaring
- THEN the command prints that it is fetching and reading `_boson.yml`, then `✓ <org/name> declared`, then deploys
- AND the declaration's warnings are printed when it ends and again under the deploy's outcome

#### Scenario: Declaration fails
- WHEN the declaration ends with an error
- THEN the command prints the error and warnings and exits 2

#### Scenario: Every entry is a pattern
- WHEN the declaration succeeds and creates no deployment
- THEN it is reported as failed with `no deployments for <org/name>; _boson.yml names no branch outright, and a pattern's deployments are created by pushes`

#### Scenario: Daemon restarts during declaration
- WHEN the daemon no longer knows the declaration
- THEN the command tells the operator to re-run `boson deploy <org/name>` and exits 2

#### Scenario: Second request while declaring
- WHEN another `boson deploy` arrives for the same project while its declaration runs, whether or not any of its deployments exist yet
- THEN it follows the running declaration and starts no deploy

### Requirement: Following a deploy
The system SHALL answer a deploy request with the id of the first pass's deploy as soon as its row exists, which is after the fetch and `_boson.yml` steps. The CLI SHALL print the log file `/var/log/boson/deploys/<id>.log` as it grows and poll the deploy row every 300 ms until it finishes.

#### Scenario: Deploy succeeds
- WHEN the deploy row finishes with status `succeeded`
- THEN the command prints `✓ deploy #<id> succeeded (<sha>)` and exits 0

#### Scenario: Deploy fails
- WHEN the deploy row finishes with status `failed`
- THEN the command prints `✗ deploy #<id> failed: <error>` and the log path, and exits 2

#### Scenario: Deploy ends before it has a row
- WHEN the deploy stops in steps 1 to 4 for a branch that has no deployment to record it against
- THEN the request is answered 422 with the reason, and the command prints it and exits 2

#### Scenario: Operator interrupts
- WHEN the operator presses Ctrl-C while the log streams
- THEN the command stops watching and exits 0, and the daemon finishes the deploy

[bug] A fetch that takes longer than the 30 second RPC timeout ends the command with exit 1 and no output, while the daemon carries on deploying.

### Requirement: One deploy per deployment at a time
The system SHALL hold a non-blocking lock keyed by repository and branch for the length of a deploy. Deployments of different branches run concurrently.

#### Scenario: Manual deploy while one runs
- WHEN `boson deploy` targets a deployment whose lock is held
- THEN the command prints `a deploy for <org/name> is already running; re-run when it finishes (boson status shows it)` and exits 3

#### Scenario: Push while one runs
- WHEN a webhook deploy finds the lock held
- THEN `deploy_pending` is set on the deployment and the running deploy picks it up

[bug] The flag needs a deployment row. A second push during a new branch's first deploy, before the row is created, is answered 202 and recorded nowhere.

### Requirement: Pipeline
The system SHALL run each deploy pass in this order, stopping at the first failure:

1. Mint an installation token.
2. Fetch the branch into `/srv/<org>/<name>/<slug>`: `git init` when the directory holds no repository, `git fetch --depth=1 --no-tags` of the branch from `https://github.com/<org>/<name>.git`, then `git reset --hard` to the fetched ref.
3. Read and validate `_boson.yml` from the checkout and find the entry matching the branch.
4. Create the deployment when the branch has none, or apply the entry's values to the existing one, and synchronise Caddy when either changed anything.
5. Insert the deploy row and open its log.
6. Check that a named environment set exists.
7. Run `docker compose config --format json` and check what it publishes.
8. Run `docker compose up -d --build --quiet-pull` in the checkout.
9. On success, prune images as specified in [deployment-lifecycle](deployment-lifecycle-spec.md).
10. Record the outcome on the deploy row.

A deploy takes the branch tip at the time it fetches.

#### Scenario: Successful deploy
- WHEN every step succeeds
- THEN the deploy row has status `succeeded`, its commit SHA and a finish time

#### Scenario: Compose up fails
- WHEN `docker compose up` exits non-zero
- THEN the deploy row has status `failed` and error `docker compose up exited <code>`

#### Scenario: Compose file does not resolve
- WHEN `docker compose config` exits non-zero
- THEN the deploy fails with `docker compose config: <stderr>` and nothing is built

### Requirement: Creating a deployment
The system SHALL create a deployment for a branch on its first deploy, when a `_boson.yml` entry matches it, with the branch, its slug, the entry's hostname, aliases, environment set and `expire_after`, and an allocated port.

#### Scenario: First push to a pattern-matched branch
- WHEN `feature/x` is pushed for the first time and the `"*"` entry matches
- THEN a deployment is created, its log records `feature/x declared: <hostname> on 127.0.0.1:<port>`, Caddy is synchronised, and the deploy continues

#### Scenario: Branch nothing declares
- WHEN a branch with no deployment is fetched and no entry matches it
- THEN the branch's checkout directory is deleted and no deployment or deploy row is written

[bug] The directory is deleted whoever created it, including the checkout an expired deployment left behind and the default-branch checkout made by declaration.

### Requirement: Port allocation
The system SHALL allocate each new deployment the lowest port from 30000 to 32767 that no active deployment holds and that can be bound on 127.0.0.1 at that moment.

#### Scenario: First deployment on the host
- WHEN no deployment exists and nothing listens on 30000
- THEN the deployment gets port 30000

#### Scenario: Port taken by another process
- WHEN a process outside boson listens on 127.0.0.1:30000
- THEN allocation skips it

#### Scenario: Range exhausted
- WHEN every port in the range is held or in use
- THEN allocation fails with `no free port between 30000 and 32767`

[bug] Allocation is not serialised across branches. Two new branches deploying at once can be given the same port. The second stops with the reason in the daemon log, creates no deployment, and succeeds on its next push.

### Requirement: Compose project name
The system SHALL pass `--project-name` on every `docker compose` invocation for a deployment. The name is `<org>/<name>-<slug>`, lowercased, with every character outside `a-z`, `0-9`, `_` and `-` replaced by `-`.

#### Scenario: Two branches of one repository
- WHEN `acme/site` deploys `main` and `develop`
- THEN their compose projects are `acme-site-main` and `acme-site-develop`

### Requirement: Published port check
The system SHALL fail a deploy before building when the resolved compose configuration does not publish exactly the deployment's allocated port on a loopback address.

#### Scenario: Nothing published
- WHEN no service publishes a port
- THEN the deploy fails, telling the operator to publish `"127.0.0.1:${BOSON_PORT}:<the port your app listens on>"`

#### Scenario: Published on every interface
- WHEN a service publishes a port with a host address other than `127.0.0.1`, `::1` or `localhost`
- THEN the deploy fails, naming the service and telling the operator to bind it to 127.0.0.1

#### Scenario: Wrong port number
- WHEN a service publishes a port other than the allocated one
- THEN the deploy fails, naming the service, the port it publishes and the port boson allocated

[bug] A published host-port range such as `8000-8010` fails the deploy with the raw parse error `The input string '8000-8010' was not in a correct format.`

### Requirement: Deploy log
The system SHALL write one log file per deploy, containing a header naming the deploy id, repository, branch and trigger, each git command and its output, the fetched commit, the environment set and its path, the output of `docker compose up`, and a final line `deploy succeeded` or `deploy failed: <error>`. The installation token is replaced with `***` wherever it would appear.

#### Scenario: Fetch URL in the log
- WHEN the log records the `git fetch` command
- THEN the URL reads `https://x-access-token:***@github.com/<org>/<name>.git`

### Requirement: Recording failures
The system SHALL record a failure against the deployment when one exists, as a deploy row with status `failed` and a log file. A failure for a branch with no deployment is written to the daemon log and returned to the caller as the reason the deploy stopped.

This covers every failure in steps 1 to 4: a failed token mint or fetch, a missing, unreadable or invalid file, a missing entry, a name another deployment serves, no free port, and a Caddy synchronisation that fails or times out. A timeout is a failure. A deploy is abandoned without a record only when the daemon itself is stopping.

#### Scenario: Caddy times out
- WHEN the Caddy synchronisation does not answer within its 10 second limit
- THEN the deploy fails with its reason and the change is put back, as for any other Caddy failure

#### Scenario: A change that cannot be put back
- WHEN the Caddy synchronisation fails and, meanwhile, another deployment has taken the name this one let go of
- THEN the deploy fails with the Caddy reason and the new values stay. The routes catch up at the next synchronisation, which is accepted (see Scope in [project.md](../project.md))

#### Scenario: Caddy refuses a changed route
- WHEN the Caddy synchronisation fails after a deployment's declared values were updated
- THEN the stored values are put back and the deploy fails with `caddy did not take the change to <hostname>: <reason>`
- AND the next deploy applies the change and synchronises again

#### Scenario: Caddy refuses a new deployment's route
- WHEN the Caddy synchronisation fails after a new deployment was created
- THEN the deployment is archived, the reason is logged, and the next push creates it again

#### Scenario: Fetch fails for an existing deployment
- WHEN the fetch fails for a branch that has a deployment
- THEN a deploy row is written with error `fetch: <reason>`, and `boson status` shows it as the last deploy

#### Scenario: File invalid on a new branch
- WHEN a branch with no deployment is pushed and its `_boson.yml` fails validation
- THEN the reason is logged by the daemon and no deploy row exists

### Requirement: Activation
The system SHALL set `webhook_active` on a deployment when a deploy of it first succeeds.

#### Scenario: First deploy fails
- WHEN a deployment's first deploy fails
- THEN `webhook_active` stays unset

### Requirement: Draining pending pushes
After each pass that reached step 5, while still holding the lock, the system SHALL check `deploy_pending`. When it is set, the system SHALL clear it and run another pass with trigger `webhook`, whether the pass before succeeded or failed, up to 3 passes in one deploy. `boson deploy` follows the first pass.

[bug] A pass that fails in steps 1 to 4 ends the deploy without checking the flag, so a push that arrived during it stays undeployed until `boson status` reports it. The flag is also not cleared when a deploy starts, so a stale one causes a second pass.

#### Scenario: Push arrives during a deploy
- WHEN a push to the same branch arrives while a deploy runs
- THEN a second pass deploys the new tip once the first ends

#### Scenario: Pushes keep arriving
- WHEN `deploy_pending` is still set after the third pass
- THEN the deploy ends with the flag set, and `boson status` reports it

### Requirement: Crash recovery
The system SHALL mark every deploy row with status `running` as `failed` with error `daemon restart` when the daemon starts.

#### Scenario: Daemon killed mid-deploy
- WHEN the daemon is killed during a deploy and restarts
- THEN that deploy's row is `failed` with error `daemon restart`
