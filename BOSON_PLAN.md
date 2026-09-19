# Boson — an opinionated single-host docker PaaS driven by GitHub Apps

## What it is

Boson is a single-binary CLI that turns one Linux host into a multi-project deployment platform. The same binary also runs as `boson serve` — a small systemd-supervised daemon that receives GitHub push webhooks. It manages:

- **Caddy** (containerised) — TLS + routing front door
- **The boson daemon** — host process; verifies pushes from GitHub and runs deploys
- **One or more containers per project** — defined by each project's own `docker-compose.yml`

`boson add` walks the GitHub App manifest flow, captures the App's credentials, configures Caddy, and fetches the checkout. Deploying is the admin's next step: place `/srv/<org>/<name>/.env` if the compose needs one (repos ship a template) and run `boson deploy`; the first successful deploy turns on push-to-deploy. Subsequent pushes flow through the webhook receiver and trigger a rebuild + restart.

**A project's internals are a black box to boson** — whatever the project's `docker-compose.yml` says, boson runs `docker compose up -d --build` from the repo root and lets the project handle the rest. Boson only needs three things per project: the repo, a public hostname, and the port Caddy should route that hostname to.

## Architecture

```
                                       Internet
                                          │
                                          ▼
 ┌──────────────────────────────────── Caddy (containerized) ───────────────────────────┐
 │  marketcanary.co              → host:8080                                             │
 │  another-app.example.com      → host:3000                                             │
 │  deploy.marketcanary.co       (admin hostname; serves /_boson/* paths only)           │
 │    /_boson/webhook/<org>/<name> → boson daemon, host:9000                            │
 │    /_boson/health             → boson daemon, host:9000                               │
 │    /_boson/setup-app/*        → boson daemon, host:9000 (App setup, during add)       │
 │    *                          → 404                                                   │
 └───────────────────────────────────────────────────────────────────────────────────────┘
                                          │
                          ┌───────────────┼───────────────────────────────┐
                          ▼               ▼                               ▼
                     market-canary    another-app                    boson daemon
                     (project's own   (project's own                 (`boson serve`, systemd,
                      compose file,    compose file,                  binds 127.0.0.1:9000;
                      binds to host    binds to host                  verifies HMAC; sole
                      port 8080)       port 3000)                     executor of deploys)
```

Caddy runs as a container. The boson daemon is the same binary as the CLI, run as `boson serve` under systemd (unit and `boson` system user created by `boson init`, removed by `boson uninstall`). CLI invocations remain one-shot processes; the daemon exists because something must hold a socket open for GitHub. It is also the **sole executor of every project action** — add, deploy, remove all run inside it, and the CLI is an API call over a local unix socket. Everything under `/srv` has exactly one writer, one user, one set of locks; every deploy fetches, the first into an empty checkout. There is no third-party webhook software — the receiver is a few hundred lines of boson using only in-box .NET (spec §14).

## Persistent state on the host

```
/usr/local/bin/boson                   # binary (user-installed)
/etc/systemd/system/boson.service      # daemon unit — written by `boson init`, removed by `boson uninstall`
/var/lib/boson/boson.db                # SQLite — source of truth (config, projects, App credentials, secrets)
/srv/<org>/<name>/                    # the checkout (docker compose build context)
```

Plus docker-managed named volumes (`caddy-data`, `caddy-config`) for Caddy's cert state — that's docker's filesystem, not boson's.

Things that go away because SQLite is enough or APIs are enough:
- No `boson.toml` or per-project `project.toml` (rows in the `projects` table)
- No `github-app.pem` or `webhook.env` files (columns)
- No `app.env` files (boson doesn't manage app env vars; the project does)
- No `Caddyfile` (pushed to Caddy via its admin API on every change)
- No platform `compose.yml` on disk (boson pipes it via stdin to `docker compose -f -`)
- No `hooks.yaml` or webhook config files (the daemon reads project rows + secrets from SQLite per request)
- No per-project `deploy.sh` (the daemon runs deploy logic in-process)
- No `mint-token.sh` (JWT signing happens in-process; .pem read from SQLite)

Project app secrets (Stripe keys, AWS keys, etc.) are the project's concern. Whatever path the project's compose file references via `env_file:` is the user's responsibility to populate.

## What boson knows about a project

Stored in SQLite, three required values per project:

| Column | Purpose |
|---|---|
| `repo` (`org/name`) | What to fetch |
| `hostname` | Public DNS name fronted by Caddy |
| `upstream_port` | Host port Caddy reverse-proxies the hostname to |

Plus the GitHub App credentials boson captures during `boson add`, in `github_*` columns so their owner is visible in the schema: `github_app_id`, `github_app_slug`, `github_installation_id`, `github_webhook_secret`, `github_app_pem`. Plus a `branch` (defaults `main`). Projects are referenced by `repo` everywhere: commands, paths, webhook URLs.

That's the whole per-project surface. Anything else (build args, container env vars, volume mounts, sidecar services, dependencies between containers) lives inside the project's own `docker-compose.yml` and is invisible to boson.

## Responsibility boundaries

Boson sits between three systems it doesn't own — GitHub, Docker and Caddy — and nearly all of its value is in the wiring between them. The boundaries are design decisions, stated here.

### Caddy is split three ways

| Concern | Owner | Consequence |
|---|---|---|
| **Configuration** | Boson | No Caddyfile exists on disk. `CaddyConfigBuilder` synthesises the whole structured-JSON config from SQLite and POSTs it to the admin API on every change — full replace, never a partial patch. Boson never parses Caddy config; it only ever overwrites it. |
| **Process lifecycle** | Docker | `restart: unless-stopped`. Boson never supervises Caddy. (Boson's own daemon is supervised separately, by systemd — see "Installs one daemon" below.) |
| **Certificates** | Caddy | LE issuance and renewal for the admin hostname and every project hostname, with state in the `caddy-data` volume. Boson never sees a private key or a renewal deadline. |

A consequence of the first row: since config arrives only over the admin API, Caddy must start with **`caddy run --resume`** so that a container or host restart reloads the last-pushed config from its autosave. Without it, Caddy would come back on the image's stock Caddyfile with every route gone, and nothing watches Caddy to notice or re-push.

### What boson owns

Six things, all of them *between* systems rather than inside any one of them:

1. **Ingress mapping** — hostname → host port, and the TLS that fronts it.
2. **GitHub identity** — one App per project: manifest flow, credential capture, PEM storage, JWT signing, installation-token minting.
3. **Webhook reception** — the `boson serve` daemon: HMAC verification, branch and activation filtering, delivery responses (403 = secret drift, 200 = verified but declined, 202 = deploy queued).
4. **Source sync** — fetch + reset at every deploy; the first deploy starts from an empty checkout.
5. **Deploy orchestration** — locking, coalescing concurrent pushes, `docker compose up -d --build`, and a durable record of what happened. All of it executes inside the daemon; the CLI only relays requests and streams results.
6. **State** — SQLite as the single source of truth. Caddy config and the systemd unit are *derived*; re-running `boson init` rebuilds them from the DB and embedded resources.

### What boson explicitly does not own

- **Anything inside a project's compose file** — build args, container env, volumes, sidecars, inter-service dependencies, database migrations, job runners. This is the black-box rule, and it's what lets boson know only three facts per project.
- **DNS.** Boson never creates or edits records. `boson add` checks the hostname resolves and warns when the address matches no local interface (common and legitimate behind NAT); whether a record actually points at this host is proven only by requesting the site over the internet.
- **Host provisioning.** Docker, the compose plugin, `git`, firewall rules, OS updates, kernel, disk. See "Installs one daemon, nothing else" below.
- **Uninstalling GitHub Apps.** `boson remove` archives the project and surfaces the App's settings URL; removing the App on GitHub's side stays a human action, so a mistaken `remove` is never destructive to the GitHub relationship.
- **Rollback.** There is no `boson rollback` in v1 — recovery from a bad deploy is to push a revert. See spec §22 [Q7].
- **Multi-host anything.** One host, no clustering, no scheduling, no service discovery.

### Trust model

Single operator, mutually trusted projects. A push to a tracked branch executes that repo's compose file against the host's Docker daemon, and Docker access is root-equivalent — so **anyone who can push to a deployed branch can take over the host and read every project's credentials**. Nothing sandboxes projects from each other or from the host; the black-box rule means boson never inspects what it runs. Deploy only repositories whose committers you would trust with root on this server, and protect tracked branches accordingly. The per-project GitHub App limits blast radius on the GitHub side only (each App reads one repo); it provides no isolation on the host.

### Installs one daemon, nothing else

Boson touches exactly five paths on the host — `/usr/local/bin/boson`, `/etc/systemd/system/boson.service`, `/var/lib/boson`, `/var/log/boson`, `/srv` — plus one `boson` system user and the Docker resources it owns. It never runs a package manager and never pipes a script to a shell. The unit file and user are boson's *own* artifacts, not third-party software: `boson init` creates them, `boson uninstall` removes them, and there is nothing on the host to patch or maintain besides the boson binary itself.

The only container boson brings is `caddy:2-alpine`, pulled from Docker Hub at `init`. There is no webhook image — the receiver is compiled into the boson binary and runs as the daemon. First install needs outbound HTTPS to Docker Hub and GitHub.

Installing packages would mean guessing the distro and package names and mutating a machine the operator controls. The boundary: **boson verifies third-party prerequisites, the user installs them; boson installs and removes its own artifacts.** A missing prerequisite produces one message listing everything absent with the command to fix each, and boson exits before creating anything.

| Dependency | Who provides it |
|---|---|
| Docker Engine, compose v2 plugin | User |
| `git` on the host | User |
| systemd as init | The OS (verified by preflight, never touched) |
| Ports 80/443 free; inbound reachable | User |
| DNS A records | User |
| Caddy container | Boson |
| boson daemon (unit file + `boson` user) | Boson — created by `init`, removed by `uninstall` |
| Project containers | The project's own compose file |
| TLS certificates | Caddy |

### What the user owns

- **DNS records** for the admin hostname and every project hostname, pointing at this host.
- **The host itself** — Docker with the compose plugin, `git`, a systemd init, ports 80/443 free and reachable from the internet, root (init needs it to create the user and unit). `boson init` verifies the software and installs none of it; ports, DNS and reachability prove themselves when the operator opens the health URL init prints.
- **The project's `docker-compose.yml`**, at the repo root, binding to `127.0.0.1:<upstream_port>` so Caddy can reach it. Boson rejects a port another boson project claims; keeping it clear of other listeners on the host is a human decision.
- **Application secrets and env** — created at `/srv/<org>/<name>/.env` after `boson add`, from the repo's template, or wherever else the project's compose `env_file:` points.
- **Two browser interactions** during `boson add`: creating the App, then installing it on the repo.
- **Env vars before the first deploy** — `boson add` fetches but doesn't deploy; if the compose needs env, create `/srv/<org>/<name>/.env` (from the repo's template) before running `boson deploy`. Webhooks stay inert until a deploy succeeds, so a half-configured project can't auto-deploy.
- **Uninstalling the GitHub App** after `boson remove`.
- **Backing up `/var/lib/boson/boson.db`.** This one is load-bearing: the DB holds every project's App private key and webhook secret, and because everything else is *derived from* it, nothing can rebuild it. Lose that file and the only path back is re-running the manifest flow for every project and cleaning up orphaned Apps on GitHub by hand. Boson ships no backup tooling and has no opinion about where copies go — but note the DB is WAL-mode with a concurrent writer, so `VACUUM INTO` is the safe way to snapshot it live, not `cp`.

## User journey

### First-time platform install on a fresh host

```bash
ssh root@<host>

curl -fL https://github.com/pingfu/boson/releases/latest/download/boson-linux-x64 \
  -o /usr/local/bin/boson && chmod +x /usr/local/bin/boson

boson init deploy.example.com
```

The `curl` above is the only third-party thing the user installs on boson's behalf. Everything else boson creates on the host is boson's own artifact, and `boson uninstall` removes it. There is no runtime to install — the binary is a self-contained .NET publish, so no .NET needs to be present.

`boson init` does:
1. Verifies the software prerequisites and **stops if any are missing**, reporting all of them at once with the fix for each: docker + compose plugin, `git`, systemd as init, root. Networking (ports, DNS, reachability) is not checked; it proves itself when the operator opens the health URL (below). Nothing is installed and nothing is created if this fails.
2. Creates the `boson` system user (no shell, member of `docker` group), `/var/lib/boson/`, `/var/log/boson/`, `/srv/`.
3. Writes `/etc/systemd/system/boson.service` (embedded template) and enables + (re)starts the daemon — it comes up on `127.0.0.1:9000`, creating and migrating `boson.db` at startup.
4. Brings up Caddy via `docker compose -f - up -d` with the YAML piped over stdin.
5. Pushes initial Caddy config via the admin API: the admin hostname routing `/_boson/*` to the daemon.
6. Caddy provisions LE cert for the admin hostname.

End state: platform is up, no projects yet. Init prints `https://deploy.example.com/_boson/health`; the operator opens it, and a 200 (served by the daemon, through Caddy) proves DNS, reachability and TLS end to end.

### Uninstalling the platform

`boson uninstall` walks `init` back. It refuses if active projects exist (use `boson remove` first, or `--force`). Two tiers:

- **Default — remove the machinery, keep the state.** Stops and removes the systemd unit, the `boson` user, and the Caddy container. Leaves `boson.db`, `/var/lib/boson`, `/var/log/boson`, `/srv` and the Caddy volumes in place, so `uninstall` → `init` is a safe round trip: a later `init` finds the DB and rebuilds everything with credentials intact.
- **`--purge` — destroy the state too.** Deletes the data directories, checkouts and Caddy volumes. Requires typed confirmation (App credentials cannot be re-issued by GitHub) and offers a final `VACUUM INTO` backup of the DB first. Surfaces each project's GitHub App settings URL — deleting the Apps stays a human action — and prints the one command boson won't run: `rm /usr/local/bin/boson`.

Details: spec §10.

### Adding a project

```bash
boson add marketcanary6/market-canary \
          --hostname marketcanary.co \
          --upstream-port 8080
```

Or with prompts for missing args. The CLI is an API call: it sends the request to the daemon over the local socket, prints the setup URL the daemon returns (opening it too if the machine has a browser; the URL is public, so on a headless host the operator opens it on their own machine), and polls for progress. The daemon:

1. **Validates**: the hostname resolves (a warning when the address matches no local interface); repo, hostname and port are each unclaimed by any other project; the port is none of 80, 443, 2019, 9000, which the platform uses.
2. **Runs the GitHub App manifest flow** on the routes it already serves: the browser lands on `/_boson/setup-app/start`, the user clicks "Create GitHub App", GitHub redirects back with a code, the daemon exchanges it for the App credentials.
3. **Captures the installation**: the browser is sent to `https://github.com/apps/<app_slug>/installations/new`; the user installs it on the repo; GitHub redirects back with the installation id.
4. **Persists the project**: one complete row — repo, hostname, port, branch, and the five `github_*` credential columns. Nothing was written before this point.
5. **Pushes Caddy config**: the hostname's reverse-proxy route. Caddy provisions an LE cert for it.
6. **Fetches the checkout** into `/srv/<org>/<name>/`.

End state on success: repo on disk, GitHub App registered and installed, Caddy routing the hostname. Nothing is running yet: the admin places `/srv/<org>/<name>/.env` if the compose needs one (starting from the repo's template) and runs `boson deploy`. Push-to-deploy switches on with the first successful deploy, so a half-configured project can't auto-deploy; until then the daemon answers each push with 200 and declines. Abandon the browser before the persist step and nothing was written; re-run to start over (an orphaned GitHub App is deleted by hand). Ctrl-C in the CLI stops only the progress display; the daemon completes or expires the flow on its own.

### Deploying the project

The first deploy is the admin's explicit `boson deploy <org/name>`, run once env is in place; the same command redeploys at any time: after a failed build, or to force a rebuild. Either way the CLI hands the request to the daemon over the local socket, streams the deploy log to the terminal, and exits with the outcome. The daemon runs:

1. **Mint a fresh installation access token.** In-process: sign an RS256 JWT with the App's PEM (from SQLite), POST it to `https://api.github.com/app/installations/<installation_id>/access_tokens`, get back a `ghs_…` token good for ~1 hour. Held in memory only, never written to disk.
2. **Refresh the working tree to the branch tip.** `git init` if the checkout is missing (the first deploy), then `git fetch` + `git reset --hard`, the same path every time. Every deploy means "deploy the latest commit". The token is passed inline on the fetch command so it never lands in `.git/config`.
3. `cd /srv/<org>/<name>` and `docker compose up -d --build`. A deploy succeeds when compose exits 0 — boson does not health-probe the app afterwards (a project that wants a health gate defines a compose `healthcheck`).
4. **Activate the webhook** (first successful deploy only): set the project's `webhook_active` flag in SQLite. The daemon checks it per request — nothing else to regenerate.

After this, every push to the project's branch triggers a deploy automatically.

Pushes that land while a deploy is already running are recorded and picked up by the running deploy before it finishes, so a burst of pushes collapses to at most one extra deploy and the newest commit ends up live. GitHub gives a webhook 10 seconds to respond and never retries a failed delivery, so boson remembers such pushes itself rather than parking them with GitHub.

`boson deploy` is also the manual redeploy path — re-running it is idempotent (same fetch + compose up).

### Commands

```
boson init <admin-hostname>   # Install the platform; re-run = rebuild derived state (recovery)
boson add <org/name> ...      # Register a project, fetch it
boson deploy <org/name>       # Manual (re-)deploy
boson list                    # All projects: status, containers, last deploy
boson remove <org/name>       # Tear down a project, archive its record
boson uninstall               # Walk back `init` (see "Uninstalling the platform")
```

(`boson serve` also exists — it's the daemon entry point, invoked by systemd, not by humans.) Everything else uses tools that already exist: `docker logs` for containers, `journalctl -u boson` for the daemon, `/var/log/boson/deploys/` for build output. Upgrading is `curl` the new binary + `systemctl restart boson`.

## Manifest flow plumbing — how `/_boson/setup-app/*` works

The daemon permanently serves `/_boson/*` behind Caddy, so GitHub's browser redirects during App setup land on routes that always exist. Each `boson add` gets a one-time state token; the setup endpoints only act on requests carrying a live token (in-memory, 15-minute TTL). An abandoned browser just lets the token expire — nothing to tear down. Full trace: spec §11.

## What boson ships in the binary

Embedded as resources in the boson binary:

- The systemd unit template (`boson.service`). Written to `/etc/systemd/system/` at `boson init`.
- The compose YAML template for the platform stack (Caddy only). Rendered to a string and piped to `docker compose -f -`.
- HTML for the manifest flow's `/start` page (auto-submitting form) and the post-callback "✓ return to your terminal" pages.

## Webhook receiver (in the daemon)

`boson serve` hosts one endpoint, `POST /_boson/webhook/<org>/<name>`, built entirely from in-box .NET (Kestrel, `System.Text.Json`, `HMACSHA256` with a constant-time compare). Per request it loads the project row from SQLite, verifies the `X-Hub-Signature-256` HMAC against the stored secret, and answers within GitHub's 10-second budget — deploys run afterwards as background work. Responses:

- **403** — signature failure. Always secret drift, always red, always an incident.
- **200** — signature verified but no deploy: a `ping` event, a push to an untracked branch, or a project not yet activated by its first deploy. All logged.
- **202** — push accepted, deploy queued.

Full endpoint contract: spec §14.

## How the webhook gets wired up (per project)

1. `boson add` submits the manifest with `hook_attributes.url = https://<admin-hostname>/_boson/webhook/<org>/<name>`.
2. GitHub creates the App with that URL, auto-generates a webhook secret.
3. The manifest exchange returns the PEM, webhook secret, App id and slug to boson; boson stores all of these in the `github_*` columns.
4. Caddy already routes the admin hostname's `/_boson/*` to the daemon (since `init`), and the daemon reads the row per request — **from here GitHub's pushes verify and get a 200, but the daemon declines to deploy them** (logged) because the project isn't active yet.
5. After the first deploy succeeds (the admin's first `boson deploy`), boson flips the project's `webhook_active` flag. Now pushes trigger deploys.

Users never touch GitHub repo settings. Each project is independent — its own App, its own secret, its own webhook URL path.

## Distribution

Single static binary, public release.

- Self-contained .NET 10 publish with `-r linux-x64 --self-contained true /p:PublishSingleFile=true`. macOS / Windows RIDs published too for development convenience; runtime target is Linux servers.
- Build via GitHub Actions workflow on tag push (in the boson repo, which is its own public repo).
- Templates embedded as resources — no separate file distribution.
- Updates: download the new release, replace `/usr/local/bin/boson`, `systemctl restart boson`.

## Migration story for marketcanary

Marketcanary becomes the first project under boson.

`docker-compose.prod.yml` is **deleted from the marketcanary repo** — the project's own `docker-compose.yml` (or just `docker-compose.prod.yml` renamed) becomes what boson runs at the repo root. The `docker/Caddyfile` is also deleted — boson generates Caddy routing. The `deploy/` directory disappears entirely.

The marketcanary repo retains:
- App code (`src/`)
- `docker/Dockerfile` (referenced by the project's compose)
- `docker-compose.yml` (the project's compose; binds to `127.0.0.1:8080:8080` so boson's Caddy can reach it)
- Migration system, jobs, etc. — unchanged
- `INFRASTRUCTURE.md` collapses to "managed by boson"
- `DEPLOYMENT.md` collapses to "boson detects pushes; see boson docs"

## Open questions

Tracked in `BOSON_SPEC.md` §22, which is authoritative.

## Implementation order

1. **`boson init`** — create the `boson` user + unit, bring up the daemon (health endpoint only) and Caddy, push the initial config (admin hostname → daemon) over Caddy's admin API. No projects. Verify HTTPS works for the admin hostname.
2. **Caddy admin-API plumbing** — full-config rebuild from DB state; boson can add and remove project routes from the running Caddy.
3. **`boson add` (no GitHub App yet)** — accept a public repo, fetch it, add the Caddy hostname route, persist to SQLite. Insert placeholder credentials (the columns are NOT NULL); step 4 replaces this path. Dev databases holding placeholder rows are throwaway; the invariant that a row means real credentials (spec §4) holds from the first release.
4. **GitHub App manifest flow** — wire `/_boson/setup-app/*` end-to-end.
5. **Token minter + private repo cloning** — replace the public-repo stub. JWT signing in-process from the .pem in SQLite.
6. **`boson deploy` + webhook endpoint** — first deploy flips `webhook_active`; the daemon's receiver goes live; first real push-to-deploy works.
7. **Remaining commands** (list, remove, uninstall).
8. **Marketcanary migration** — cut over the live droplet from the current standalone stack to boson.

Each step is its own commit / release. Boson is shippable for "single-project use" after step 6.
