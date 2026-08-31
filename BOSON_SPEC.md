# Boson — Implementation Spec

Companion to `BOSON_PLAN.md`. The plan describes *what boson is and what it does*; this spec describes *how it's built*. Where the plan handwaves an interaction, this spec pins it down.

Open questions and judgement calls are flagged inline with **[Q]**. Decisions taken below are defaults, not commitments — push back on any of them.

---

## 0. Internal architecture at a glance

One binary, two modes: every CLI command is a short-lived process, and `boson serve` is the single resident mode — a systemd-supervised daemon that receives GitHub pushes (via Caddy) and is the **sole deploy executor**: `boson deploy` in a shell relays the request to it over a unix socket rather than deploying itself (§8). Everything below the Commands layer is wiring to systems boson doesn't own (GitHub, Caddy, Docker, systemd); SQLite is the one place state lives.

```
   operator shell                      systemd unit boson.service
   `boson <command>`                   `boson serve` — the one resident process:
          │                            Kestrel on 127.0.0.1:9000; GitHub pushes
          │                            arrive via Caddy; deploys run in-process
          │                                        │
          ▼                                        ▼
┌────────────────────────────────────────────────────────────┐
│ Program.cs — System.CommandLine wiring + DI root           │
│ one self-contained binary: every CLI command is a          │
│ short-lived process; `serve` is the one resident mode      │
└─────────────────────────────┬──────────────────────────────┘
                              ▼
┌────────────────────────────────────────────────────────────┐
│ Commands/ — init · add · deploy · list · remove ·          │
│ uninstall · serve                                          │
└─────────────────────────────┬──────────────────────────────┘
                              ▼  orchestrates
┌────────────────────────────────────────────────────────────┐    external systems each module fronts
│ domain modules                                             │
│                                                            │
│ Github/    ManifestFlowOrchestrator (runs in the daemon,   │
│            §11) ───────────────────────────────────────────┼──▶ user's browser (via Caddy)
│            InstallationTokenMinter, GithubClient ──────────┼──▶ api.github.com · github.com
│                                                            │
│ Caddy/     CaddyConfigBuilder (JSON from DB state, §7),    │
│            CaddyAdminClient ───────────────────────────────┼──▶ Caddy admin API · 127.0.0.1:2019
│                                                            │
│ Serve/     WebhookEndpoint (HMAC verify, §14),             │
│            DaemonHost (Kestrel on 127.0.0.1:9000, §8) ─────┼──▶ GitHub pushes + setup redirects (via Caddy) · CLI RPC (socket)
│                                                            │
│ Deploy/    Deployer (§6),                                  │
│            ProjectLocks (in-daemon, §16) ──────────────────┼──▶ git · docker compose
│                                                            │
│ Platform/  StackOrchestrator, ComposeTemplate (§9),        │
│            SystemdUnit (§8) ───────────────────────────────┼──▶ docker compose (caddy) · systemctl
└─────────────────────────────┬──────────────────────────────┘
                              │  every module reads/writes
                              ▼  state through Storage/
┌────────────────────────────────────────────────────────────┐
│ Storage/ — Db, Migrator, Projects/Platform/Deploys repos   │
│ SQLite at /var/lib/boson/boson.db — THE source of truth:   │
│ Caddy config and the daemon's behaviour derive from it;    │
│ re-running `boson init` rebuilds derived state (§15)       │
├────────────────────────────────────────────────────────────┤
│ Util/ — ProcessRunner · GitCli · DockerCli · DnsResolver   │
│ Resources/ — embedded platform compose YAML,               │
│ boson.service unit, caddy-init.json, SQL migrations,       │
│ manifest-flow HTML                                         │
└────────────────────────────────────────────────────────────┘
```

Reading it top to bottom mirrors a request's life: an invocation enters through `Program.cs`, a command orchestrates one or more domain modules, each module talks to exactly one external boundary, and every fact any of them relies on comes out of Storage/. The daemon is the same picture — `serve` is just the one command that never exits.

---

## 1. Tech stack

| Concern | Choice | Rationale |
|---|---|---|
| Language / runtime | C# / .NET 10, single-file self-contained publish | Already specified in plan. Linux-x64 is the runtime target; osx-arm64 and win-x64 are dev convenience. |
| CLI parsing | `System.CommandLine` (2.0 GA) | Native subcommands, completion, rich help; first-party. |
| SQLite | `Microsoft.Data.Sqlite` + thin `Dapper` layer for queries | Avoid EF Core — schema is small, migrations are hand-written. |
| HTTP server (the daemon) | Kestrel via `WebApplication.CreateSlimBuilder` | Minimal APIs; binds to `127.0.0.1` + the unix socket only. |
| Webhook receiver | Built in: Kestrel + `HMACSHA256` + `CryptographicOperations.FixedTimeEquals` | ~200 lines, all in-box (§14). No third-party webhook software to install, configure or patch. |
| Daemonisation | systemd unit, `Type=simple`, `Restart=always` | No `UseSystemd`/`Type=notify` — avoids the `Microsoft.Extensions.Hosting.Systemd` package; restart-on-exit suffices (§8). |
| HTTP client (Caddy admin, GitHub) | `HttpClient` + `System.Text.Json` | No Octokit; we only call ~3 GitHub endpoints. |
| JWT signing (GH App) | `System.IdentityModel.Tokens.Jwt` + `Microsoft.IdentityModel.Tokens` | RS256 from PEM-encoded private key. |
| Console UX | `Spectre.Console` | Tables, status spinners, prompts. |
| Process exec | `System.Diagnostics.Process` wrapped in our `ProcessRunner` | No third-party shellout lib. |
| Git | Shell out to `git` binary | A host prerequisite, verified by `boson init` preflight (§10) and never installed by boson. Avoids LibGit2Sharp's native binary drag in single-file publish. |
| Docker | Shell out to `docker` / `docker compose` | Same reasoning. |
| Tests | xUnit + `Verify.Xunit` for snapshot tests | |
| Logging | `Microsoft.Extensions.Logging` → console + rolling file at `/var/log/boson/boson.log` | |

**[Q1]** Trim publishing — disabled for v1 because `System.CommandLine` and `Microsoft.IdentityModel` don't trim cleanly. Binary will be ~50–80 MB. Revisit if size matters.

---

## 2. Repository layout

```
boson/
├── BOSON_PLAN.md
├── BOSON_SPEC.md
├── Boson.sln
├── README.md
├── .github/workflows/
│   ├── ci.yml                              # build + test on PR
│   └── release.yml                         # tag-triggered release
├── src/Boson/
│   ├── Boson.csproj
│   ├── Program.cs                          # System.CommandLine wiring + DI root
│   ├── Commands/
│   │   ├── InitCommand.cs
│   │   ├── AddCommand.cs
│   │   ├── DeployCommand.cs
│   │   ├── ListCommand.cs
│   │   ├── RemoveCommand.cs
│   │   ├── UninstallCommand.cs
│   │   └── ServeCommand.cs
│   ├── Storage/
│   │   ├── Db.cs                           # connection factory + WAL config
│   │   ├── Migrator.cs                     # embedded .sql files, runs on every command
│   │   ├── ProjectsRepository.cs
│   │   ├── PlatformRepository.cs           # key/value: admin_hostname, binary_version, ...
│   │   └── DeploysRepository.cs
│   ├── Caddy/
│   │   ├── CaddyAdminClient.cs             # POST /load, GET /config
│   │   └── CaddyConfigBuilder.cs           # builds the JSON doc from DB state
│   ├── Github/
│   │   ├── ManifestFlowOrchestrator.cs     # App-setup flow, hosted by the daemon (§11)
│   │   ├── InstallationTokenMinter.cs      # JWT + access_token exchange
│   │   └── GithubClient.cs                 # 3 endpoints
│   ├── Serve/
│   │   ├── DaemonHost.cs                   # Kestrel bootstrap, startup recovery, shutdown
│   │   └── WebhookEndpoint.cs              # request validation pipeline (§14)
│   ├── Deploy/
│   │   ├── Deployer.cs                     # token + fetch + compose up
│   │   └── ProjectLocks.cs                 # per-project in-daemon locks (§16)
│   ├── Platform/
│   │   ├── StackOrchestrator.cs            # platform compose up/down
│   │   ├── ComposeTemplate.cs
│   │   └── SystemdUnit.cs                  # write/verify/remove boson.service (§8)
│   ├── Resources/                          # all embedded .resx-style content
│   │   ├── boson.service
│   │   ├── platform-compose.yaml
│   │   ├── caddy-init.json
│   │   ├── manifest-start.html
│   │   ├── manifest-success.html
│   │   └── migrations/
│   │       ├── 0001_init.sql
│   │       └── ...
│   └── Util/
│       ├── ProcessRunner.cs
│       ├── DockerCli.cs
│       ├── GitCli.cs
│       └── DnsResolver.cs
└── test/Boson.Tests/
    ├── Boson.Tests.csproj
    ├── Snapshots/                          # Verify outputs
    └── ...
```

Single `Boson.csproj`. No premature split into Core/Cli — every module is reachable from a single CLI entry point.

---

## 3. Filesystem layout on the host

```
/usr/local/bin/boson                       # binary (user-installed; upgrade = replace + restart daemon)
/etc/systemd/system/boson.service          # daemon unit — written by init, removed by uninstall (§8)
/var/lib/boson/                            # owned boson:boson
├── boson.db                               # SQLite, mode 0600
├── boson.sock                             # CLI→daemon RPC socket (§8); file perms are the auth
├── locks/
│   └── _platform.lock                     # init/uninstall mutual exclusion (flock)
└── tmp/                                   # transient scratch
/var/log/boson/
├── boson.log                              # structured JSONL, rolling 10 MB × 5
└── deploys/
    └── <deploy-id>.log                    # captured stdout+stderr per deploy
/srv/<org>/<name>/                         # project dir, named by the repo
├── repo/                                  # the checkout = compose build context
└── .env                                   # project env, if its compose references it
```

---

## 4. SQLite schema

`Resources/migrations/0001_init.sql`:

```sql
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;

CREATE TABLE schema_version (
  version    INTEGER PRIMARY KEY,
  applied_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE platform (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
-- Known keys:
--   admin_hostname        e.g. "deploy.example.com"
--   installed_at          ISO-8601
--   binary_version        boson version that last touched this DB

CREATE TABLE projects (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  repo             TEXT NOT NULL,                   -- "org/name", stored lowercase; the project's identifier everywhere
  hostname         TEXT NOT NULL,
  upstream_port    INTEGER NOT NULL CHECK (upstream_port BETWEEN 1 AND 65535),
  branch           TEXT NOT NULL DEFAULT 'main',

  -- github_* columns are issued by GitHub during the manifest flow (§11); GitHub cannot re-issue them
  github_app_id          INTEGER NOT NULL,
  github_app_slug        TEXT    NOT NULL,          -- GitHub's slug for the App; used in its URLs
  github_installation_id INTEGER NOT NULL,
  github_webhook_secret  TEXT    NOT NULL,
  github_app_pem         TEXT    NOT NULL,

  webhook_active   INTEGER NOT NULL DEFAULT 0,      -- 1 once first deploy succeeded
  deploy_pending   INTEGER NOT NULL DEFAULT 0,      -- push arrived mid-deploy; drained by §6 step 9
  created_at       TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at       TEXT NOT NULL DEFAULT (datetime('now')),
  archived_at      TEXT
);

CREATE UNIQUE INDEX uniq_active_repo     ON projects(repo)          WHERE archived_at IS NULL;
CREATE UNIQUE INDEX uniq_active_hostname ON projects(hostname)      WHERE archived_at IS NULL;
CREATE UNIQUE INDEX uniq_active_port     ON projects(upstream_port) WHERE archived_at IS NULL;

CREATE TABLE deploys (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  project_id  INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  trigger     TEXT NOT NULL,                        -- 'manual' | 'webhook'
  commit_sha  TEXT,
  started_at  TEXT NOT NULL DEFAULT (datetime('now')),
  finished_at TEXT,
  status      TEXT NOT NULL DEFAULT 'running',      -- 'running' | 'succeeded' | 'failed'
  log_path    TEXT NOT NULL,
  error       TEXT
);

CREATE INDEX idx_deploys_project_started ON deploys(project_id, started_at DESC);
```

Notes:
- Projects are identified by `repo` (`org/name`), stored lowercase since GitHub compares names case-insensitively. Uniqueness is **active-scoped**: repo, hostname and port are unique among non-archived rows, so `boson remove` releases all three and re-adding the same repo works. `deploys` references the surrogate `id`, so history follows its archived row and `--purge` cascades it away instead of orphaning it. Every "by repo" lookup means "the active row with that repo".
- Every `github_*` column is `NOT NULL`: a project row is inserted only once `boson add` has everything (§11). A row existing means the project is fully added.
- `deploy_pending` is the coalescing flag. A verified push that finds a deploy already running sets it; the in-flight deploy drains it before releasing the lock by running one more deploy of the branch tip. Set on an idle project means a redeploy was dropped — `boson list` surfaces it.
- `archived_at` keeps the row around after `boson remove` — useful for incident forensics. Hard-delete is a separate `--purge` flag.
- All persisted secrets (`github_webhook_secret`, `github_app_pem`) live in plaintext; the DB is mode 0600. Encryption-at-rest is **[Q2]** — defer.

---

## 5. CLI surface (every command, every flag)

Exit codes:
- `0` success
- `1` user error (bad args, validation failed)
- `2` runtime failure (docker/git/network/daemon unreachable)
- `3` lock contention (deploy already running)
- `99` internal bug (§18)

### `boson init`
```
boson init <admin-hostname>
```
Steps spelled out in §10. Preflight never installs anything — see §10. Idempotent: re-running it against an existing install rebuilds all derived state (§15), which makes it the recovery command too. The daemon port is fixed at 9000 (loopback only); preflight checks it is free.

### `boson add`
```
boson add <org/name>
          --hostname <host>
          --upstream-port <port>
          [--branch <branch>]          # default: main
```
The CLI is an API call: it POSTs the request over `boson.sock`, prints the setup URL the daemon returns (and opens it if the machine has a browser — the URL is public, served through Caddy, so the browser can be anywhere), and polls for progress. The daemon runs the entire flow (§11) — manifest exchange, credential capture, row insert, Caddy route, first deploy (which performs the clone). Nothing is persisted until the flow has everything; the row is inserted whole.

A failed first deploy (typically missing env) leaves the project added and inactive, with the fix printed: place `/srv/<org>/<name>/.env`, run `boson deploy <org/name>`. Abandoned earlier: nothing was persisted; re-run to start over. The only possible residue is an orphaned GitHub App, deleted by hand.

### `boson deploy`
```
boson deploy <org/name>
```
The CLI does not run the deploy — the daemon is the sole deploy executor (§8). The command:
1. Connects to `/var/lib/boson/boson.sock` and requests a deploy (`trigger='manual'`).
2. On 202: receives the deploy id, tails `/var/log/boson/deploys/<id>.log` to the terminal, polls the row until `finished_at`, and exits 0 on `succeeded`, 2 on `failed`.
3. On 409 (a deploy for this project is already running): exit 3 with a clear message.
4. Daemon unreachable: exit 2, message points at `systemctl status boson` / `journalctl -u boson`.

Webhook-triggered deploys never contend visibly: on a busy project lock the Deployer sets `deploy_pending` and returns `Coalesced` (§6, §16).

### `boson list`
```
boson list [--json]
```
Per project: repo | hostname | port | branch | webhook active | container status (`docker compose ps`) | last deploy (status, commit, time, log path) | a warning if `deploy_pending` is set on an idle project (recovery: `boson deploy`).

### `boson remove <org/name>`
```
boson remove <org/name> [--purge]
```
An RPC like `deploy` — the daemon:
- Stops project containers, `docker compose down`
- Removes the Caddy route
- Sets `archived_at`. With `--purge`: hard-delete row (deploy history cascades), `rm -rf /srv/<org>/<name>`. App on GitHub side is **not** uninstalled (user does that — boson surfaces the URL).

### `boson serve`
```
boson serve [--port <n>]                # --port is DEV-ONLY (foreground runs on a workstation);
                                        # production always binds 9000, which Caddy's route targets
```
The daemon entry point — invoked by systemd, not by humans (§8). Two listeners: Kestrel on `127.0.0.1:9000` serving `/_boson/health`, `/_boson/webhook/{org}/{name}` (§14) and the App-setup endpoints (§11), and the unix socket `/var/lib/boson/boson.sock` serving the CLI RPC (§8). Foreground; logs to stdout (journald) + `/var/log/boson/boson.log`. Runs on any OS for development; the unit file exists only on the Linux host.

### `boson uninstall`
```
boson uninstall [--force] [--purge]
```
Walks `init` back (§10). Refuses while non-archived projects exist unless `--force`. Default keeps all data; `--purge` destroys it after typed confirmation. Idempotent.

Upgrading boson is not a command: replace `/usr/local/bin/boson` (same `curl` as install), `systemctl restart boson`. Migrations run on every invocation, so the next command or daemon start upgrades the schema.

---

## 6. Module contracts

### `IProjectsRepository`
```csharp
Project? GetByRepo(string repo);
IReadOnlyList<Project> ListActive();
void Insert(Project p);                 // complete row only (§11); throws on repo/host/port collision
void MarkWebhookActive(string repo);
void SetDeployPending(string repo, bool value);
void Archive(string repo);
void Purge(string repo);
```

### `ICaddyAdminClient`
```csharp
Task<JsonDocument> GetConfigAsync();
Task LoadConfigAsync(JsonDocument cfg);     // POST /load — full replace
```
**No** path-level mutations. Boson always builds the full config from DB state and POSTs it.

### `CaddyConfigBuilder`
```csharp
JsonDocument Build(IReadOnlyList<Project> projects, string adminHostname);
```

### `ManifestFlowOrchestrator`
Runs inside the daemon (§11): owns the pending-setup entries (in-memory, keyed by state token, 15-minute TTL) and handles the `/_boson/setup-app/*` requests. On completion it inserts the project row and starts the first deploy; the CLI observes progress through the `/add/status` RPC.

### `InstallationTokenMinter`
```csharp
Task<InstallationToken> MintAsync(string repo, CancellationToken ct);
public sealed record InstallationToken(string Value, DateTimeOffset ExpiresAt);
```
Reads `github_app_id` + `github_installation_id` + `github_app_pem` from SQLite, signs a short-lived RS256 JWT, exchanges it at GitHub for a `ghs_…` installation access token. Details in §13.

**No cross-deploy cache.** Every deploy pass mints fresh — even in the daemon, where a cache could technically live, it would only save one HTTP call per deploy while adding an invalidation problem. The token is held for the lifetime of one pass of the deploy loop: a coalescing drain that loops back (§6 step 9) passes through step 3 again and mints a new one.

### `Deployer`
```csharp
Task<DeployResult> DeployAsync(string repo, DeployTrigger trigger, CancellationToken ct);
```
Every deploy means "deploy the branch tip, now" — no target commit is passed. Branch and activation filtering happen *before* this is called: the daemon's `WebhookEndpoint` declines untracked refs and inactive projects and never invokes the Deployer for them (§14). `DeployAsync` assumes a valid, active target.

1. Acquire the project's in-daemon lock — `ProjectLocks.TryEnter(repo)`, non-blocking. Plain process-local state is sufficient because the daemon is the sole deploy executor (§8, §16). If held:
   - `trigger=='webhook'` → set `deploy_pending`; return `Coalesced`. The running deploy will pick it up at step 9.
   - `trigger=='manual'` → return `LockHeld` (the RPC answers 409; the CLI exits 3).
2. Open new `deploys` row, `status='running'`, log to `/var/log/boson/deploys/<id>.log`.
3. `InstallationTokenMinter.MintAsync(repo)` — returns a short-lived `ghs_…` token (see §13).
4. If `/srv/<org>/<name>/repo/` is missing: `git clone --depth=1`. Otherwise `GitCli.FetchAndResetAsync(repoPath, repo, branch, token)` — see §13. Working tree is now at the branch tip.
5. `UPDATE deploys SET commit_sha = <git rev-parse HEAD>`.
6. `docker compose -f /srv/<org>/<name>/repo/docker-compose.yml up -d --build` (cwd = repo dir; pass `--quiet-pull`).
7. If webhook_active==0 (first successful deploy): mark `webhook_active=1`. Activation is purely this DB flag — the daemon checks it per request.
8. Update `deploys` row `status='succeeded'|'failed'`, `finished_at`. Success means `docker compose up` exited 0 — boson does not probe the app afterwards. Health checking is descoped: a project that wants one defines a compose `healthcheck`, which is inside the black box.
9. **Drain `deploy_pending`** — still holding the lock: if set, clear it and loop back to step 2 (`trigger='webhook'`, a new `deploys` row) — the tip has moved since this deploy fetched it.

    Draining happens whether the deploy succeeded or failed — a newer commit is often the fix for a broken one. Bounded at **3 passes** per invocation; if `deploy_pending` is still set after that, leave it, log a warning, and let `boson list` surface it rather than looping indefinitely under a push flood.
10. Release lock.

Steps 2–9 are the loop body. A burst of N pushes during one deploy therefore costs at most one extra deploy, and the newest commit always wins — each pass deploys whatever the tip is at fetch time.

---

## 7. Caddy admin API plumbing

### Reaching the admin API

Caddy's admin API is published to the host at `127.0.0.1:2019` (loopback only).

In `platform-compose.yaml`:
```yaml
caddy:
  image: caddy:2-alpine
  ports:
    - "80:80"
    - "443:443"
    - "127.0.0.1:2019:2019"   # admin API; not internet-reachable
```

Boson's `CaddyAdminClient` uses `HttpClient` against `http://127.0.0.1:2019`. No `docker exec` indirection; no JSON escaping problems.

### Reaching project containers from Caddy

Caddy is in a container; project containers bind to `127.0.0.1:<port>` on the host. We use Docker's `host.docker.internal` (resolved via `extra_hosts: host-gateway` on the caddy service):
```yaml
caddy:
  extra_hosts:
    - "host.docker.internal:host-gateway"
```

A project route's upstream is `host.docker.internal:<upstream_port>`.

### Caddy config shape

We use Caddy's structured JSON config, not Caddyfile. One server (`main`) listening on 80/443, routes synthesized from DB:

```json
{
  "apps": {
    "http": {
      "servers": {
        "main": {
          "listen": [":80", ":443"],
          "routes": [
            {
              "match": [{"host": ["deploy.example.com"], "path": ["/_boson/*"]}],
              "handle": [{"handler": "reverse_proxy", "upstreams": [{"dial": "host.docker.internal:9000"}]}]
            },
            {
              "match": [{"host": ["marketcanary.co"]}],
              "handle": [{"handler": "reverse_proxy", "upstreams": [{"dial": "host.docker.internal:8080"}]}]
            },
            {
              "match": [{"host": ["deploy.example.com"]}],
              "handle": [{"handler": "static_response", "status_code": 404}]
            }
          ]
        }
      }
    }
  }
}
```

Every state-changing command rebuilds this whole doc and `POST /load`s it.


### Config durability across restarts

Config reaches Caddy only over the admin API; there is no Caddyfile on disk (see the responsibility table in the plan). The failure mode that creates is silent and total:

The `caddy:2-alpine` image's default command is `caddy run --config /etc/caddy/Caddyfile`. Caddy autosaves each admin-API-loaded config to `/config/caddy/autosave.json`, and the `caddy-config` volume persists it — but nothing *reads* it back unless the process is started with `--resume`. So with the stock command, a `docker restart boson-caddy` or a host reboot brings Caddy up on the image's default Caddyfile: no admin hostname, no project routes, every site down — and nothing watches Caddy to detect it or re-push (the boson daemon does not supervise Caddy).

The platform stack therefore pins:

```yaml
command: ["caddy", "run", "--resume"]
```

`--resume` loads the autosaved config when one exists and falls back to `--config` otherwise, so `boson init` on a fresh host still bootstraps correctly: Caddy starts on the stock Caddyfile, boson polls the admin endpoint and POSTs the real config (§10 step 7), and that push becomes the autosave that survives every subsequent restart.

Reboots therefore need nothing from the user: Caddy resumes its own config, systemd restarts the daemon. Re-running `boson init` (§15) is for drift, never for reboots.

**Boson never edits Caddy config in place** — it only ever replaces it wholesale from DB state. Combined with `--resume`, the live config has exactly two possible origins: the last `POST /load` boson made, or that same config restored from autosave. There is no third state to debug.

---

## 8. The boson daemon (`boson serve`)

The one resident boson process, and the **sole executor of every project action** — add, deploy, remove all run inside it; the CLI is an API call. Every write to `/srv` happens in this process, as the `boson` user (the first deploy of a project clones; later ones fetch). Two listeners:

- TCP `127.0.0.1:9000`, fronted by Caddy for TLS: `/_boson/health`, the webhook endpoint (§14), and the App-setup redirect endpoints (§11 — protected by their one-time state tokens, not HMAC).
- Unix socket `/var/lib/boson/boson.sock`: the CLI RPC (below). Never exposed over TCP.

Webhook deploys run as in-process background tasks calling `Deployer.DeployAsync`; CLI requests arrive over the socket and run the same way. No fork/exec, no container, no second copy of the binary anywhere.

### CLI RPC over the unix socket

| Request | Behaviour | Responses |
|---|---|---|
| `POST /add` `{repo, hostname, port, branch}` | Validates, creates a pending-setup entry, returns the browser URL (§11) | 200 `{setupUrl, token}` · 409 collision |
| `GET /add/status/{token}` | Progress of a pending add | 200 `{phase, deployId?, error?}` · 404 unknown or expired token |
| `POST /deploy/{org}/{name}` (`trigger=manual`) | Starts `Deployer.DeployAsync` as a background task (clones first if the checkout is missing — §6 step 4) | 202 `{deployId}` · 409 already running · 404 unknown project |
| `POST /remove/{org}/{name}` (`purge?`) | Compose down, drop Caddy route, archive (or purge) | 200 · 404 |

Auth is the socket file itself: `boson.sock`, mode 0660, owner `boson:boson` — root and the daemon's own user can connect, nothing else can. No tokens, no TLS, no request signing needed. The CLI observes a started deploy through the artifacts that already exist: it tails `/var/log/boson/deploys/<id>.log` and polls the `deploys` row (§5).

Being the sole executor is what makes the rest of this section simple: per-project locking is process-local (§16), startup recovery is unconditional (below), `/srv` and the deploy logs have exactly one writer, and git never sees a mixed-ownership repo.

### Unit file

`Resources/boson.service`, written to `/etc/systemd/system/boson.service` by `boson init` (§10, rewritten on re-run if drifted), removed by `boson uninstall`:

```ini
[Unit]
Description=boson daemon
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=simple
User=boson
Group=boson
ExecStart=/usr/local/bin/boson serve
WorkingDirectory=/var/lib/boson
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/boson/.net
NoNewPrivileges=true
Restart=always
RestartSec=2
TimeoutStopSec=600

[Install]
WantedBy=multi-user.target
```

- `Type=simple`, no `sd_notify`: `Restart=always` covers crashes and readiness is observable at `/_boson/health`; avoids the `Microsoft.Extensions.Hosting.Systemd` package.
- `TimeoutStopSec=600`: shutdown legitimately waits for an in-flight deploy (below).
- `After=docker.service`: deploys shell out to `docker compose`.

### The `boson` user

System user, no shell; member of the `docker` group; owns `/var/lib/boson`, `/var/log/boson`, `/srv`. Created by `init`, removed by `uninstall`. Hygiene, not a security boundary — `docker` group membership is root-equivalent. CLI commands require root (they manage the user, the unit, and these directories). Because the daemon executes every deploy and clone, the CLI's write surface under boson-owned paths is just the DB (and its WAL/SHM sidecars) — the one rule is that the CLI chowns those to `boson:boson` after touching them, so the daemon never loses write access.

### Lifecycle

- **Startup recovery** — before listening, mark any `deploys` rows at `status='running'` as `failed`, `error='daemon restart'`. Unconditional and sound *because* the daemon is the sole deploy executor: a `running` row without a live daemon task can only be a crash orphan. (The in-flight row exists to anchor the deploy id and log path; the table's real job is recording outcomes.)
- **Graceful shutdown** — on SIGTERM: stop accepting requests, let an in-flight deploy finish (bounded by `TimeoutStopSec`), exit 0. A deploy that is cut off anyway is caught by startup recovery on the next start.
- **Upgrade** — replace the binary, `systemctl restart boson`. One binary on the host; daemon and CLI cannot version-skew.
- **Crash** — systemd restarts within 2s. Requests in the gap get a 502 from Caddy: red in GitHub's Recent Deliveries (§12).
- **Dev** — `boson serve` runs foreground on any OS; the unit file is a Linux-host concern only.

### Deploy execution inside the daemon

`WebhookEndpoint` responds 202, then runs `Deployer.DeployAsync(repo, Webhook)` on a background task; failures land in the `deploys` row, never as unobserved exceptions. CLI-requested deploys enter through the RPC and become identical tasks, so contention is arbitrated entirely by `ProjectLocks` in-process (§16). There is no in-memory work queue: `deploy_pending` *is* the queue (§6 step 9), and it survives a daemon restart, which an in-memory flag would not.

---

## 9. Platform compose stack

`Resources/platform-compose.yaml` (templated; rendered to a string and piped via stdin to `docker compose -f -`):

```yaml
name: boson

services:
  caddy:
    image: caddy:2-alpine
    container_name: boson-caddy
    restart: unless-stopped
    command: ["caddy", "run", "--resume"]     # see §7 "Config durability across restarts"
    ports:
      - "80:80"
      - "443:443"
      - "127.0.0.1:2019:2019"
    extra_hosts:
      - "host.docker.internal:host-gateway"
    volumes:
      - caddy-data:/data
      - caddy-config:/config

volumes:
  caddy-data:
  caddy-config:
```

One service. The daemon is not in the stack — it is a host process (§8) that Caddy reaches via `host.docker.internal:9000`, exactly like project upstreams. No secrets appear in container env or `docker inspect` output; the daemon reads them from SQLite per request.

---

## 10. `boson init` — step by step

1. **Preflight** — verify every host prerequisite; abort without touching anything if any is missing. Detailed below.
2. **User**: create the `boson` system user (no shell, member of `docker` group).
3. **Filesystem**: create `/var/lib/boson/`, `/var/lib/boson/locks/`, `/var/lib/boson/tmp/`, `/var/log/boson/deploys/`, `/srv/`. Modes 0700 / 0600, owned `boson:boson`. Trees that already exist are `chown -R`'d to the (possibly new) `boson` UID — the uninstall→init round trip deletes and recreates the user, so ownership of kept data must be re-established here.
4. **DB**: open `/var/lib/boson/boson.db`, run all embedded migrations. Insert platform rows: `admin_hostname`, `installed_at`, `binary_version`.
5. **Daemon**: write `/etc/systemd/system/boson.service` (§8), `systemctl daemon-reload`, `systemctl enable --now boson`. Poll `127.0.0.1:9000/_boson/health` until 200 (10s timeout).
6. **Compose stack**: render `platform-compose.yaml` (Caddy only), `docker compose -f - up -d`.
7. **Caddy bootstrap**: poll `127.0.0.1:2019/config/` until 200 (10s timeout), then `POST /load` with the initial config (admin hostname routing `/_boson/*` to the daemon; no projects yet).
8. **Verify**: `curl https://<admin-hostname>/_boson/health` returns 200 within 90s (LE issuance time). On failure, surface `docker logs boson-caddy` and `journalctl -u boson` tails.

Idempotent: re-running `init` against an existing install rebuilds all derived state — full Caddy config from the DB (every project, not just the admin hostname), the unit file, the user, directory ownership — which makes it the recovery command after restoring a DB backup or manual meddling (§15). Preflight runs again on every invocation.

### Step 1 in detail — preflight verifies, never installs

**Boson installs no third-party software on the host.** No package manager is ever invoked, no `curl | sh`, no `apt`/`apk`/`yum`. Boson writes only to `/var/lib/boson`, `/var/log/boson`, `/srv`, `/usr/local/bin/boson` and `/etc/systemd/system/boson.service`, and creates one `boson` system user — all of them boson's own artifacts, all removed by `boson uninstall`. If a prerequisite is missing, boson reports exactly what is missing and stops.

Installing packages would mean guessing the distro and package names and mutating a machine the operator controls, so boson reports and stops instead. That puts the weight on the preflight: it must never treat "I couldn't tell" as "fine".

| # | Requirement | Check | Failure class |
|---|---|---|---|
| 1 | Docker Engine, running and reachable | `docker info` exits 0 | fatal |
| 2 | Compose v2 plugin | `docker compose version` exits 0, version ≥ 2.0 | fatal |
| 3 | Running as root | euid 0 — init creates the `boson` user and the unit file | fatal |
| 4 | **`git` on the host** | `git --version` exits 0 | fatal |
| 5 | systemd is the init system | `/run/systemd/system` exists and `systemctl` on PATH | fatal |
| 6 | A usable port-inspection tool | `ss` present, else `netstat`, else fall back to a direct bind attempt on :80/:443 | fatal if none available |
| 7 | Ports 80 and 443 free | via #6 | fatal |
| 8 | Admin hostname resolves here | A/AAAA for the given admin hostname matches one of the host's public IPs | fatal |
| 9 | Outbound HTTPS reachability | HEAD against `registry-1.docker.io`, `github.com`, `api.github.com` | fatal |

Notes on the ones that aren't obvious:

- **#4 `git`.** boson shells out to `git` for the clone in `boson add` and the fetch in every deploy (§13). Discovering it missing at clone time — *after* the user has created and installed a GitHub App — leaves an orphaned App to clean up by hand.
- **#5 systemd.** WSL default distros, containers, and some minimal VMs aren't systemd hosts; the daemon can't be supervised there. Failing this loudly at preflight beats a cryptic `systemctl` error at step 5.
- **#6, and why it is its own row.** The previous formulation, `ss -tln | grep -E ':80 |:443 '`, returns empty when `ss` itself is absent, and empty was read as "ports are free". A safety check that passes because its own tooling is missing is worse than no check at all. Absence of every probing method is a fatal preflight failure in its own right, not a silent pass.
- **#9.** `init` pulls `caddy:2-alpine` from Docker Hub; `boson add` and every deploy talk to GitHub. On an egress-filtered host these fail mid-flow with errors that say nothing about which allowlist entry is missing. Probing first turns that into one clear sentence.

**All checks run before any of them aborts.** Boson collects every failure and prints them together with the remediation for each, rather than failing on the first and making the user rediscover the next one on each re-run. Exit code 1 (user error), no stack trace.

```
$ boson init deploy.example.com
✗ Preflight failed — 2 problems. boson does not install host dependencies; please resolve these and re-run.

  git not found on PATH
      Required to clone and update project repositories.
      Debian/Ubuntu:  apt-get install -y git
      Alpine:         apk add git

  Port 443 is already bound (nginx, pid 812)
      Caddy needs 80 and 443. Stop the conflicting service and re-run.

No changes were made.
```

That last line matters: preflight runs before step 2, so a failed `init` has created no user, no unit file, no directories, no database and no containers. There is nothing to clean up and re-running is always safe.

### `boson uninstall` — walking init back

Refuses while non-archived projects exist (lists them, points at `boson remove`) unless `--force`. Two tiers, both idempotent:

**Default — remove machinery, keep state:**
1. `systemctl disable --now boson`; delete `/etc/systemd/system/boson.service`; `daemon-reload`.
2. `docker compose down` the platform stack (Caddy).
3. `userdel boson`.
4. Keep `boson.db`, `/var/lib/boson`, `/var/log/boson`, `/srv`, and the Caddy volumes. Print what was kept.

`uninstall` → `init` is a supported round trip: a later `init` finds the existing DB, re-runs migrations, recreates user + unit, re-chowns the kept trees to the new user's UID (init step 3), and rebuilds all derived state — all credentials intact. This makes reinstalling boson a safe recovery move.

**`--purge` — destroy state too:**
- Typed confirmation naming the project count and stating that App credentials cannot be re-issued by GitHub.
- Offers a final `VACUUM INTO` backup of `boson.db` to a user-supplied path before deleting.
- Deletes `/var/lib/boson`, `/var/log/boson`, the `/srv` project trees, Caddy volumes.
- Prints each project's GitHub App settings URL (deleting Apps stays a human action) and the one command boson won't run itself: `rm /usr/local/bin/boson`.

---

## 11. `boson add` — manifest flow in detail

The daemon owns the whole flow; the CLI is an API call. The setup URL is public, so the browser session runs anywhere — the usual case is a headless host with the browser on the operator's own machine. GitHub's redirects land on the `/_boson/setup-app/*` routes the daemon always serves through Caddy.

```
[CLI] boson add <org/name> --hostname Y --upstream-port Z
   │
   ├─ RPC POST /add {repo, hostname, port, branch} over boson.sock
   │   Daemon validates (collisions, hostname A record), creates an
   │   in-memory pending-setup entry keyed by a random state token,
   │   returns the setup URL.
   │
   ├─ Print (and open, if a browser exists here) →
   │   https://<admin-hostname>/_boson/setup-app/start?state=<token>
   ├─ Poll RPC GET /add/status/<token> until done, streaming progress
   │
   │   [daemon, public listener — the same routes Caddy always serves]
   │
   │   GET /start?state=<token>
   │   ────────────────────────
   │   Serves manifest-start.html: an auto-submitting <form>
   │   POSTing to https://github.com/settings/apps/new?state=<token>
   │   with the manifest JSON:
   │     - name: "boson-<name>" (cosmetic; boson keys on the returned app id)
   │     - url: https://github.com/<repo>
   │     - redirect_url: https://<admin-hostname>/_boson/setup-app/callback
   │     - setup_url: https://<admin-hostname>/_boson/setup-app/installed
   │     - hook_attributes.url: https://<admin-hostname>/_boson/webhook/<org>/<name>
   │     - public: false
   │     - default_events: ["push"]
   │     - default_permissions: { contents: "read", metadata: "read" }
   │
   │   GET /callback?code=...&state=...
   │   ───────────────────────────────
   │   Match state to the pending entry (unknown → 404).
   │   POST https://api.github.com/app-manifests/<code>/conversions.
   │   Receive { id, slug (the App's), pem, webhook_secret, ... }. Hold in
   │   the pending entry. Serve a 302 →
   │   https://github.com/apps/<app_slug>/installations/new?state=<token>
   │   (GitHub carries state through to the setup_url redirect)
   │
   │   GET /installed?installation_id=<id>&setup_action=install&state=<token>
   │   ───────────────────────────────────────────────────────────────────────
   │   Match state to the pending entry (unknown → 404).
   │   Capture installation_id into the pending entry.
   │   Serve manifest-success.html: "✓ Return to your terminal."
   │
   │   [daemon, continuing in-process]
   │   ├─ Insert the complete project row (all credentials NOT NULL — §4)
   │   ├─ Push Caddy config: add the hostname route → host.docker.internal:<port>
   │   └─ Run the first deploy (§6; it clones, since no checkout exists).
   │       On success: webhook_active=1.
   │
   └─ CLI sees status=done (or failed): prints summary, or the env-file
       path + `boson deploy` retry command after a failed first deploy.
```

### Failure and cancellation

Nothing is persisted before the "insert the complete project row" step. Pending-setup entries are in-memory with a 15-minute TTL: an abandoned browser or a daemon restart lets the entry evaporate — re-running `boson add` starts over. Ctrl-C in the CLI stops only the progress display: the daemon owns the flow, so a browser session that completes anyway still inserts the row and runs the first deploy, visible afterwards in `boson list`. A status poll for a token the daemon no longer holds answers 404; the CLI reports the add must be re-run. The only possible residue of an abandoned flow is an orphaned GitHub App, deleted by hand on GitHub. After the row insert, the project exists normally and any deploy failure is recovered with `boson deploy`.

---

## 12. Push-to-deploy request flow

The end-to-end trace for a single `git push` after `boson add` (including its first deploy) has succeeded. Every step here is a real network hop or process boundary.

```
[Developer]                        git push origin main
[GitHub]                           App fires webhook event
[Internet]  ─┬──────────────────►  POST https://deploy.example.com/_boson/webhook/<org>/<name>
             │                     X-Hub-Signature-256: sha256=<hmac>
             │                     Body: full push event JSON
             ▼
[Caddy]        matches route:  host=deploy.example.com AND path=/_boson/*
               reverse_proxy to  host.docker.internal:9000  (the boson daemon, §8)
             │
             ▼
[daemon]       WebhookEndpoint — the §14 pipeline, synchronous, sub-second:
                  1. body ≤ 25 MB, buffered whole                 (over → 413)
                  2. load project row for <org>/<name> from SQLite (unknown → 404)
                  3. HMAC-SHA256(body, github_webhook_secret) == header? (no → 403)
                  4. event == ping?                               (yes → 200)
                  5. body parses as a push event with a ref?      (no → 400)
                  6. payload.ref == refs/heads/<branch>?          (no → 200 "ignored")
                  7. webhook_active == 1?                         (no → 200 "inactive")
                  all pass → respond 202 {"status":"queued"}
               then, on a background task, Deployer.DeployAsync — the §6 steps:
                  1. ProjectLocks.TryEnter(<org/name>)  — in-daemon, §16
                     (if already held: set deploy_pending; done)
                  2. INSERT INTO deploys (trigger='webhook', ...)
                  3. Mint installation access token (§13)
                  4. git fetch <token-URL> --depth=1 --no-tags branch tip
                     git reset --hard origin/<branch>
                  5. UPDATE deploys SET commit_sha = HEAD
                  6. docker compose up -d --build         ─► rebuilds app image,
                                                            recreates container
                  7. first-success activation (no-op here; webhook_active is already 1)
                  8. UPDATE deploys SET status='succeeded', finished_at=now
                  9. drain deploy_pending — if a push landed during 3–8,
                     loop to 2 and deploy the (newer) tip
                  10. release the project lock
             │
             ▼
[GitHub]       delivery recorded with the synchronous phase's status code
               (visible in App → Advanced → Recent Deliveries)
```

Zero Caddy-config changes throughout — the route to `<hostname>` is unchanged; only the container behind `host.docker.internal:<port>` is now the new build.

### The HTTP response is not the deploy outcome

**GitHub marks a delivery failed if the response takes longer than 10 seconds**, and does *not* automatically redeliver failures — while a real deploy takes minutes. So the daemon responds after validation only, before any deploy work starts. The status codes carry exactly two meanings: **403 = the signature didn't verify** (secret drift — an incident); **200/202 = the signature verified** — 200 when boson declines (ping, untracked branch, inactive project), 202 when a deploy was queued. A 202 never means *"a deploy succeeded"*: outcomes are read from `boson list` and the deploy log, not from GitHub's delivery log, and lock contention becomes `deploy_pending` rather than an error response.

### NAT hairpin

`init` step 8's verify curl calls the host's own public hostname *from* the host, which relies on NAT hairpin working for the host's own public address — normal on a plain Linux VPS, broken on some providers' networking. It runs at install time, so a broken hairpin surfaces early with a clear failure. **Verify on the real host during the marketcanary migration.**

### Failure modes

| What breaks | Where it's caught | What the developer sees |
|---|---|---|
| Wrong HMAC (secret drift DB ↔ GitHub) | receiver step 3 | **403 — red in Recent Deliveries.** The daemon reads the secret from SQLite directly, so a mismatch means the DB and GitHub disagree. Won't self-heal; recovery is `boson remove` + `boson add`. |
| Push to a non-tracked branch | receiver step 6 | 200 `{"status":"ignored"}`; logged, never an error. |
| Push before the first successful deploy (`webhook_active=0`) | receiver step 7 | 200 `{"status":"inactive"}`; deploys stay off until a deploy succeeds — normally `boson add`'s final step (§6 step 7). |
| Two pushes land in quick succession | Deployer step 1 (project lock held) | Both got 202. The second deploy's task finds the lock held and sets `deploy_pending`; the in-flight deploy drains it before releasing the lock (§6 step 9), so the newest commit wins. A burst of N pushes costs at most one extra deploy. |
| Manual `boson deploy` runs concurrently with a webhook deploy | Deployer step 1 (same lock) | Webhook side coalesces as above (GitHub already got its 202). A *manual* request that loses the lock gets 409 from the RPC; the CLI exits 3 with a clear message — the human re-runs it. |
| Daemon down when the operator runs `boson deploy` | CLI RPC connect | Exit 2, message points at `systemctl status boson` / `journalctl -u boson`. systemd normally has the daemon back within 2s (§8); if the binary itself is broken, a local deploy would be equally broken — same binary. |
| Installation token mint fails (App uninstalled, PEM stale) | Deployer step 3 | Deploy row = `failed` with `error='token mint: <detail>'`. Cause is App-level and won't self-heal; surfaced by `boson list`. |
| `git fetch` fails (network, repo deleted, permissions revoked) | Deployer step 4 | Deploy row = `failed`. Old container keeps running. |
| Compose build fails | Deployer step 6 | Deploy row = `failed`, `log_path` has the full build output. Old container keeps running (compose leaves it in place on build failure). |
| Build succeeds but app doesn't come up | not detected by boson | Deploy row = `succeeded` — boson's contract ends at `docker compose up` exiting 0 (health probing is descoped; a project that wants one defines a compose `healthcheck`). Diagnose with `docker logs`. |
| Daemon killed mid-deploy (SIGKILL, OOM, power) | §8 startup recovery | Row left at `running` is marked `failed`, `error='daemon restart'`, on next daemon start. Recover with `boson deploy <org/name>`. |
| Delivery fails outright (daemon down, host down, Caddy down) | GitHub side | 502 from Caddy or a timeout — red. **GitHub does not automatically redeliver.** systemd restarts a crashed daemon within 2s (§8); recover with the "Redeliver" button under App → Advanced → Recent Deliveries, or just run `boson deploy <org/name>` — it resets to the branch tip, which reaches the same end state. |
| Manual redelivery of an already-deployed push | receiver → Deployer | Fresh deploy runs. Working tree is already at that SHA and Docker's layer cache is warm, so it's a fast no-op. Harmless. |
| GitHub's `ping` at App creation races the manifest callback | receiver step 2 | GitHub fires one `ping` the moment the App exists, which can arrive before `boson add` inserts the row — one red 404 delivery. Cosmetic and one-time; do not debug it. Every later ping answers 200 `pong`. |

### Observability paths

- `boson list` — per-project container status, last deploy, pending redeploys.
- `/var/log/boson/deploys/<id>.log` — captured fetch+build output per deploy (`boson deploy` streams it live).
- `docker logs` — project container output.
- `journalctl -u boson` — every incoming request logged with its status code and, for declines, the reason (also in `/var/log/boson/boson.log`).
- GitHub App page → Advanced → Recent Deliveries — raw payload, response code, "Redeliver" button.

---

## 13. Installation token minting & fetching updates

Every deploy needs to pull the latest commit from a private repo. Boson has no long-lived deploy key — it has the App's PEM in SQLite, and mints a fresh installation access token per deploy.

### Minting

Two HTTP calls, both in-process:

**Step A — sign a JWT with the App's private key.**
```csharp
var jwt = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
    issuer: appId.ToString(),
    claims: new[] { new Claim("iat", nowUnix - 60), new Claim("exp", nowUnix + 540) },
    signingCredentials: new SigningCredentials(new RsaSecurityKey(rsaFromPem), SecurityAlgorithms.RsaSha256)));
```
- `iat`: now − 60s (clock-skew backdate GitHub requires).
- `exp`: now + 540s (GitHub's cap is 10 min; leave headroom).
- `iss`: the numeric `github_app_id`.

**Step B — exchange for an installation token.**
```
POST https://api.github.com/app/installations/<installation_id>/access_tokens
Authorization: Bearer <jwt>
Accept: application/vnd.github+json
```
Response contains `{ "token": "ghs_...", "expires_at": "..." }`. Token lives ~1 hour, scoped to just the installation's repos with just the manifest's declared permissions (`contents:read`, `metadata:read`).

Held in memory for the lifetime of one deploy pass (see the no-cache note under `InstallationTokenMinter` in §6). **Never written to disk.**

### Fetching with the token

`GitCli.FetchAndResetAsync(repoPath, repo, branch, token)`. Every deploy takes the branch tip:
```bash
cd /srv/<org>/<name>/repo
git fetch https://x-access-token:<token>@github.com/<org>/<name>.git \
          --depth=1 --no-tags \
          +refs/heads/<branch>:refs/remotes/origin/<branch>
git reset --hard refs/remotes/origin/<branch>
```

Details:
- **Token passed inline on `git fetch`, not via `git remote set-url`.** Keeps the token out of `.git/config` on disk; it exists only in the argv of one short-lived `git` process.
- **`--depth=1` + `--no-tags`.** Deploys don't care about history.
- **Forced ref update (`+…`).** Tolerates force-pushes on the tracked branch.

### Working-tree hygiene

`reset --hard` restores tracked files but leaves **untracked** files alone, and boson does not run `git clean`. Two consequences:

- A file deleted from the repo, or a build artefact written into the checkout by a previous `docker build`, persists in the build context indefinitely. In practice Dockerfiles copy what they need by name, so this is inert; a project that needs a pristine context can `docker build --no-cache` via its own compose config.
- Anything a user drops inside the checkout survives deploys. This is the safer failure direction, but it is **not** the supported way to supply env vars — those belong at `/srv/<org>/<name>/.env`, one level *above* the clone (see the plan's deploy section), precisely so the choice here stays reversible. If we ever add `git clean -fdx`, only that convention keeps it from deleting user data.

### Argv-exposure caveat

The token is visible in `ps aux` on the host while `git fetch` is running. On a single-tenant deploy host this doesn't matter; on shared-tenant hosts it would. Ship with URL-in-argv; if we ever run boson somewhere multi-UID, switch to:
```bash
git -c http.extraheader="Authorization: Bearer <token>" fetch https://github.com/<org>/<repo>.git ...
```
(same real exposure — the header value is still in argv — but less obvious to casual `ps`). Or a `GIT_ASKPASS` helper script for full opacity.

### Why fetch, not re-clone

The clone happens on the first deploy (`git clone --depth=1`, same token handling — §6 step 4). Subsequent deploys reuse the local object database — nothing to download for unchanged blobs, and Docker's build-context cache stays warm.

---

## 14. Webhook receiver endpoint

`WebhookEndpoint` in the daemon (§8). One route: `POST /_boson/webhook/{org}/{name}`. All in-box .NET — Kestrel, `System.Text.Json`, `HMACSHA256`, `CryptographicOperations.FixedTimeEquals`. Validation pipeline, in order, each step short-circuiting:

| # | Check | On failure / short-circuit |
|---|---|---|
| 1 | Body ≤ 25 MB — GitHub's own webhook payload cap, so no legitimate delivery is ever rejected for size; read fully (HMAC needs the raw bytes) | 413 |
| 2 | Project row for `{org}/{name}` exists and is not archived | 404 |
| 3 | `X-Hub-Signature-256` present; HMAC-SHA256(raw body, `github_webhook_secret`) matches via `FixedTimeEquals` | **403** — secret drift, red in Recent Deliveries |
| 4 | `X-GitHub-Event: ping`? | 200 `{"status":"pong"}` — GitHub sends one at App creation; handled, not an error |
| 5 | Body parses as a push event with `ref` | 400 |
| 6 | `ref == refs/heads/<branch>` | 200 `{"status":"ignored"}` + log line |
| 7 | `webhook_active == 1` | 200 `{"status":"inactive"}` + log line |

All pass → **202** `{"status":"queued"}`, then `Deployer.DeployAsync(repo, Webhook)` on a background task (§8) — which deploys the branch tip, so the payload's commit list is irrelevant. The response is sent before any deploy work starts, so GitHub's 10-second budget is met by construction.

Semantics: **403 = signature failure = incident; 200 = verified but declined; 202 = verified and queued.** Red always means broken, green always means fine — including a project whose first deploy hasn't succeeded yet, which answers 200 `inactive` rather than erroring.

The signature covers the raw request bytes: the body is buffered first and the HMAC checked before `System.Text.Json` ever parses attacker-controllable input. Secrets are read from SQLite per request — no cache, no derived copy to drift.

---

## 15. Recovery

Everything on the host is **derived** (rebuildable from SQLite), **irreplaceable** (only in SQLite), or **user-supplied**:

| Artefact | Class | Recovered by |
|---|---|---|
| Caddy live config | derived | re-running `init`, or Caddy's own autosave (§7) |
| systemd unit `boson.service` | derived | re-running `init` rewrites from the embedded template (§8) |
| `/srv/<org>/<name>/repo` checkouts | derived | the next deploy re-clones (§6 step 4) |
| `/srv/<org>/<name>/.env` | user-supplied | the user re-creates it; boson never holds a copy |
| Caddy's LE certs | derived | Caddy re-issues (mind LE rate limits) |
| **`boson.db`** | **irreplaceable** | nothing — see below |

(The daemon has no derived config at all — it reads SQLite per request, so there is nothing of it to rebuild beyond the unit file.)

`boson.db` holds each project's App private key (`github_app_pem`) and `github_webhook_secret`. GitHub issues these once during the manifest flow and they **cannot be re-read afterwards**. Losing the DB means re-running `boson add` for every project and deleting the orphaned Apps on GitHub by hand. Rebuilding is powerless here — everything derives *from* the thing that's gone.

Boson has no backup command and takes no position on backup tooling; keeping a copy of `/var/lib/boson/boson.db` off-host is stated as a user responsibility in the plan. One implementation note if that changes, or for anyone writing the cron job: the DB is WAL-mode with the daemon writing concurrently, so `cp` can capture a torn or incomplete snapshot. `VACUUM INTO` (or SQLite's online backup API) is the consistent way to copy it live.

The recovery command is re-running `boson init <admin-hostname>`: idempotent, rebuilds every derived artefact (§10) — full Caddy config from the DB, unit file, user, directory ownership. Use it after restoring the DB from a backup or after manual meddling. A reboot needs nothing — `--resume` (§7) restores Caddy, systemd restarts the daemon.

---

## 16. Concurrency model

| Operation | Lock |
|---|---|
| All deploys (webhook-triggered and CLI-requested) | Per-project non-blocking lock inside the daemon (`ProjectLocks`). Valid as plain process-local state because the daemon is the sole deploy executor (§8) — there is no second process to race. Manual contention → RPC 409 → CLI exit 3; webhook contention → `deploy_pending` + `Coalesced` (§6 step 9), GitHub already got its 202 (§14). |
| Caddy admin updates | Mutex inside the daemon, which makes all steady-state config pushes (add/remove). The only other writer is `boson init` at bootstrap/recovery — not concurrent with normal operation. |
| DB writes | SQLite WAL + `busy_timeout=5000` (daemon and CLI processes write concurrently). |
| `boson init` / `uninstall` | Process-wide flock on `/var/lib/boson/locks/_platform.lock` — these contend CLI-vs-CLI across processes, so a filesystem lock is still the right tool there. |

---

## 17. Logging & telemetry

- Console output: human-readable, Spectre.Console (CLI); plain structured lines to stdout in `serve` mode, captured by journald.
- File log: `/var/log/boson/boson.log`, JSONL, rolling 10 MB × 5. Every command logs `{ts, command, args, outcome, duration_ms}`; the daemon additionally logs every webhook request `{ts, repo, event, status, reason}`.
- Per-deploy log: stdout+stderr of the `git fetch` / `git reset` (token redacted from the echoed URL) and `docker compose up --build`, written to `/var/log/boson/deploys/<id>.log`. Path stored in `deploys.log_path`. Each pass of the coalescing loop (§6 step 9) opens its own `deploys` row and its own log file.
- No remote telemetry. **[Q5]** Anonymous usage ping? Default no.

---

## 18. Error handling philosophy

- **User errors** (bad repo, port collision, missing DNS): exit 1 with a single clear line; no stack trace.
- **External failures** (docker daemon down, GitHub 5xx, LE rate limit): exit 2 with one line + tail of the relevant log. Suggest the recovery command (re-run `boson init`, `docker logs boson-caddy`).
- **Internal bugs** (assertion violations): exit 99 with full stack trace. These should be impossible; if seen, file an issue.
- Every command is re-runnable. Every state-mutating step is checkpointed in the DB or filesystem so a Ctrl-C never leaves an unrecoverable mess.

---

## 19. Build & release

`.github/workflows/release.yml`:

```yaml
on:
  push:
    tags: ['v*']

jobs:
  build:
    strategy:
      matrix:
        include:
          - { os: ubuntu-latest, rid: linux-x64,  artifact: boson-linux-x64 }
          - { os: ubuntu-latest, rid: linux-arm64, artifact: boson-linux-arm64 }
          - { os: macos-latest,  rid: osx-arm64,  artifact: boson-osx-arm64 }
          - { os: windows-latest, rid: win-x64,   artifact: boson-win-x64.exe }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet publish src/Boson/Boson.csproj
              -c Release -r ${{ matrix.rid }}
              --self-contained true
              /p:PublishSingleFile=true
              /p:DebugType=embedded
              /p:Version=${{ github.ref_name }}
              -o publish
      - uses: actions/upload-artifact@v4
        with:
          name: ${{ matrix.artifact }}
          path: publish/boson*

  release:
    needs: build
    runs-on: ubuntu-latest
    steps:
      - uses: actions/download-artifact@v4
      - uses: softprops/action-gh-release@v2
        with:
          files: '**/boson*'
          generate_release_notes: true
```

Tag push → release. Artifact filenames stable across versions so `curl …/latest/download/boson-linux-x64` works.

`ci.yml` runs on PRs: `dotnet build` + `dotnet test`. No publish.

---

## 20. Testing strategy

| Layer | Approach |
|---|---|
| Pure logic (`CaddyConfigBuilder`, `SystemdUnit`) | xUnit + Verify snapshots. Cover edge cases (config with 0/1/N projects). |
| `WebhookEndpoint` (§14) | xUnit with a test server + tempfile SQLite. The pipeline in order: oversized body → 413; unknown/archived project → 404; bad or missing signature → 403 (and the compare is `FixedTimeEquals`); ping → 200; malformed payload → 400; untracked ref → 200 ignored; inactive → 200; valid push → 202 and `DeployAsync` invoked. |
| `ProjectsRepository` etc. | xUnit against an in-memory or tempfile SQLite. |
| `ManifestFlowOrchestrator` | xUnit with `WebApplicationFactory`-style test server. Stub GitHub calls via `HttpMessageHandler`. |
| `Deployer` | Integration test with a fake compose project (a single nginx container) on Linux runners only. Gated behind `BOSON_INTEGRATION=1` env. |
| Deploy coalescing (§6 step 9) | xUnit against `ProjectLocks` + tempfile SQLite, no docker. Cases: contention sets `deploy_pending` and returns `Coalesced`; drain loops exactly once; 3-pass bound holds under a synthetic flood and leaves `deploy_pending` set. |
| `GitCli` clone + fetch/reset (§13) | Integration test against a throwaway GitHub repo: first call clones, second call after a new push lands the new tip; force-push on the branch is tolerated. Gated behind `BOSON_INTEGRATION=1`. |
| `init` preflight (§10) | xUnit with a stubbed `ProcessRunner`. The cases that matter are the negative ones: `git` absent is fatal; *all* of `ss`/`netstat`/bind unavailable is fatal rather than a pass; multiple simultaneous failures are all reported in one run, not just the first. |
| Caddy `--resume` (§7) | Integration test: `boson init`, POST a config, `docker restart boson-caddy`, assert the admin hostname still answers. This is the regression test for the reboot failure mode — it fails on the stock image command. Gated behind `BOSON_INTEGRATION=1`. |
| End-to-end | Manual on a throwaway VPS initially; automate later. Reboot the VPS as part of it — a host restart is the one path with no boson process involved to paper over a mistake. |

Snapshot tests live in `test/Boson.Tests/Snapshots/`. Reviewed via `Verify`'s diff tooling.

---

## 21. Open questions (consolidated)

- **[Q1]** Trim publishing — defer.
- **[Q2]** DB encryption-at-rest — defer.
- **[Q5]** Anonymous usage telemetry — default off.
- **[Q6]** Repo location & licensing — `github.com/pingfu/boson`. Licence still TBD.
- **[Q7]** Manifest flow `/start` page authentication beyond the state token — defer; the token is one-time with a 15-minute TTL, and the endpoints act only on live tokens (§11).
- **[Q8]** Hostname aliases — defer.
- **[Q12]** `boson rollback` — not in v1. The primitives exist (`reset --hard` to a SHA, `deploys.commit_sha` history); the objection is that it lets the host and the repo disagree with nothing recorded on GitHub's side. Revisit with operational experience.
