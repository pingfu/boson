# Boson: design notes

Design for work that has no implementation yet. The running system is described by `src/Boson/` and its comments.

Items carry a **[Q]** tag where a decision is still open. Tags are stable identifiers; gaps in the sequence are settled questions.

---

## 1. Registry mode: running images GitHub built

Boson runs a project two ways. **Source mode** fetches the repo and builds on the host (`DockerCli.ComposeUpBuildAsync`, `up -d --build`), and is what exists today. **Registry mode** fetches the repo for its compose file, pulls an image CI already built, and starts it. The compose file decides which.

Building on the host puts the heaviest work of a deploy on the machine serving every other project's traffic: a Node or Rust build competes for CPU and RAM with live containers, and build cache grows until an operator prunes it. Registry mode moves that work to a GitHub-hosted runner and reduces the host's deploy to a pull and a container swap. It also puts build failures in Actions, where the committer is already looking, instead of in `/var/log/boson/deploys/<id>.log` on the server.

Source mode stays the lower floor: a repo with a Dockerfile and a compose file deploys with no pipeline of its own.

### Mode selection

After the fetch in `Deployer.RunPassAsync`, the deployer runs `docker compose config --format json` in the checkout, with `BOSON_IMAGE_TAG` already set (below) so tag interpolation resolves. A compose file where no service declares `build` is registry mode; anything else is source mode. The result is written to `projects.deploy_mode`, and `WebhookEndpoint` reads that stored value to decide which event to act on.

A project that switches modes costs one failed deploy: the stored mode is still the old one when the switching commit's event arrives, so that pass runs the wrong path. The failed pass still fetched and reset, so the mode it records is correct and the next event deploys normally. `boson list` shows the current mode.

### The image tag is the commit sha

Boson exports `BOSON_IMAGE_TAG` into the environment of every `docker compose` invocation in a registry-mode pass, set to the deployed commit sha. The compose file interpolates it:

```yaml
services:
  web:
    image: ghcr.io/org/my-app:${BOSON_IMAGE_TAG}
    ports:
      - "127.0.0.1:8080:8080"
    env_file: .env
```

Process environment beats the project directory's `.env` in compose interpolation, so a committed `.env` cannot shadow the value boson passes. A repo that wants `docker compose up` to work by hand on a developer machine writes `${BOSON_IMAGE_TAG:-latest}`.

Pinning to the sha makes the compose file and the image come from the same commit, and makes "deploy sha X" a complete instruction. That is what a `boson rollback` needs ([Q7]).

### Registry authentication

The installation token mints through `InstallationTokenMinter` unchanged, and authenticates to GHCR as well as to git:

```bash
docker login ghcr.io -u x-access-token --password-stdin   # token on stdin
```

`ProcessRunner.RunAsync` already takes `stdin`, so the token stays out of argv here.

`docker login` writes credentials to `$DOCKER_CONFIG/config.json`, which the minter's contract otherwise forbids: the token is held in memory for one deploy pass and never written to disk. The narrower contract registry mode upholds: the deployer creates `/var/lib/boson/docker/<deploy-id>` with mode 0700, points `DOCKER_CONFIG` at it for the login and every compose invocation in the pass, and deletes the directory when the pass ends, on success and on failure. The token reaches disk only inside that directory, owned by the `boson` user, for the length of one deploy.

The App manifest declares `packages: read`. GHCR grants an installation token access to a package only when the package inherits access from the repo the App is installed on, which is the default for images a repo's own workflow pushes. A package created outside that path, or with inherited access removed, fails the login with nothing to indicate the cause is a package setting rather than the token.

### The trigger is `workflow_run`, not `push`

A push means a build has started. Acting on it pulls a tag CI has not published yet, so registry mode subscribes to `workflow_run` and acts on the run that publishes the image:

- `action == "completed"`
- `workflow_run.conclusion == "success"`
- `workflow_run.head_branch == projects.branch`
- `workflow_run.path == ".github/workflows/deploy.yml"`

The path filter keeps a repo's other workflows out of the deploy path. A lint workflow finishing before the publishing one would otherwise deploy a sha with no image; one finishing after would deploy the same sha twice. The filename is a convention with no override ([Q8]).

The deploy target is `workflow_run.head_sha`, not the branch tip. Source mode deploys the tip because the tip is what it can build; registry mode can only run a sha whose image exists, and the tip's may not. `GitCli` fetches that sha directly:

```bash
git fetch <token-URL> --depth=1 --no-tags <sha>
git reset --hard FETCH_HEAD
```

GitHub serves any reachable sha, so this needs no extra refspec on the remote.

`boson deploy <org/name>` resolves the branch tip and deploys that sha, which fails when CI has not published it. The failure is a pull error naming the missing tag.

### Changes to what exists

**Manifest** (`ManifestFlowOrchestrator`). Both events and the extra permission ship from the start, so no App is edited when a repo changes mode:

```
default_events: ["push", "workflow_run"]
default_permissions: { contents: "read", metadata: "read", packages: "read" }
```

**Schema**, `Resources/migrations/0002_registry_mode.sql`:

```sql
ALTER TABLE projects ADD COLUMN deploy_mode TEXT NOT NULL DEFAULT 'source';  -- 'source' | 'registry'
ALTER TABLE projects ADD COLUMN deploy_pending_sha TEXT;                     -- registry mode: the sha to drain to
```

`deploy_pending` stays a boolean for source mode, where the drain target is always the tip. In registry mode the drain target is a specific commit, so a `workflow_run` landing mid-deploy writes its sha to `deploy_pending_sha` alongside the flag; a later one overwrites it, so the newest published image wins and a burst still costs at most one extra deploy through the drain loop in `Deployer.DeployAsync`.

**Webhook pipeline** (`WebhookEndpoint`). Steps 5 and 6 branch on `deploy_mode`:

| # | Source mode | Registry mode |
|---|---|---|
| 5 | body parses as a push event with `ref` | body parses as a `workflow_run` event |
| 6 | `ref == refs/heads/<branch>` | `action`, `conclusion`, `head_branch` and `path` all match |

An event of the other mode's type answers 200 `{"status":"ignored"}`, the same as an untracked branch. Steps 1 to 4 and 7 are unchanged, as are the response semantics: 403 is a signature failure, 200 is verified and declined, 202 is verified and queued.

**Compose invocation**, with `BOSON_IMAGE_TAG` and `DOCKER_CONFIG` in the environment:

```
docker compose --project-name <org>-<name> up -d --quiet-pull
```

No `--build`.

### Failure modes

| What breaks | Where it surfaces | Result |
|---|---|---|
| CI build fails | GitHub Actions | No successful `workflow_run`, so no delivery and no `deploys` row. The running container is untouched. |
| `docker login ghcr.io` fails (`packages: read` missing, package access not inherited from the repo) | Deployer, before compose up | Deploy row `failed`, `error='registry login: <detail>'`. Cause is App-level and won't self-heal. |
| Image tag absent (publish still in flight, or the workflow published a different tag) | Deployer, at compose up | Compose pull fails, deploy row `failed`, log names the missing reference. Old container keeps running. |
| Compose file and image disagree (a service renamed in the commit, image built from another) | not detected by boson | Same contract as source mode: success is `docker compose up` exiting 0. |

**Every deploy leaves the previous sha's image on the host.** Nothing prunes them, so disk fills at roughly one image per deploy until an operator runs `docker image prune` ([Q9]).

### What the repo provides

Two files, against source mode's one:

- `docker-compose.yml` at the root, services declaring `image:` and no `build:`, the web-facing one binding `127.0.0.1:<upstream_port>` and its tag interpolating `${BOSON_IMAGE_TAG}`.
- `.github/workflows/deploy.yml` with `permissions: { contents: read, packages: write }`, building on push to the tracked branch and pushing `ghcr.io/<org>/<name>:${{ github.sha }}`.

---

## 2. Testing gaps

- **Snapshot coverage.** `CaddyConfigBuilderTests` and `SystemdUnitTests` assert against inline expectations. Moving them to `Verify` snapshots in `test/Boson.Tests/Snapshots/` gets diff tooling on config shapes, where an unintended change is easy to miss by eye.
- **End-to-end is manual**, on a throwaway VPS. Automating it needs a disposable host with real DNS. Include a reboot: a host restart is the one path with no boson process involved to paper over a mistake.

---

## 3. Open questions

- **[Q2]** DB encryption-at-rest. `github_app_pem` and `github_webhook_secret` are plaintext in a 0600 file. Defer.
- **[Q3]** Anonymous usage telemetry. Default off if it ever lands.
- **[Q4]** Licence. The repo is `github.com/pingfu/boson` with no licence file, which leaves it all-rights-reserved by default.
- **[Q5]** Manifest flow `/start` authentication beyond the state token. Defer; the token is one-time with a 15-minute TTL, and the endpoints act only on live tokens.
- **[Q6]** Hostname aliases beyond the `www` redirect.
- **[Q7]** `boson rollback`. The primitives exist (`reset --hard` to a SHA, `deploys.commit_sha` history); the objection is that it lets the host and the repo disagree with nothing recorded on GitHub's side. Registry mode (§1) answers that objection for the projects using it: the image published for a sha is a GitHub-side record of what runs, and `BOSON_IMAGE_TAG` makes a sha a complete deploy instruction. Source mode still has only the host's word for it. Revisit once registry mode has operational history.
- **[Q8]** Registry mode's publishing workflow is fixed at `.github/workflows/deploy.yml` (§1). A `projects.deploy_workflow` column plus `boson add --deploy-workflow` would make it configurable; defer until a repo needs a different filename.
- **[Q9]** Registry-mode image retention (§1). Pruning images no compose project references is a candidate for the end of a deploy pass; defer until a host actually fills.
- **[Q10]** Token exposure in argv. `git fetch` carries the installation token in its URL, visible in `ps aux` for the length of the fetch. This is acceptable on a single-tenant host. Running boson anywhere multi-UID means switching to `git -c http.extraheader="Authorization: Bearer <token>"` (same real exposure, less obvious to casual `ps`) or a `GIT_ASKPASS` helper for full opacity.
