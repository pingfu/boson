# boson

Push-to-deploy for a single Linux server.

Boson runs on your production host and automatically clones your GitHub repos and runs each one with `docker compose up -d --build`, routes a public hostname to each with automatic TLS using Caddy, and redeploys on every push.

Per project it takes two inputs: the repo and a public hostname.

![Your CI pipeline stays; boson absorbs the image registry, deploy step, reverse proxy and TLS renewal that would otherwise sit between a merge and a live container.](assets/boson-architecture-light.svg)

Boson is for the small-server deployment shape where you want a GitHub push to update a private Docker Compose app, but you do not want GitHub Actions to SSH into production, do not want to maintain GitHub-hosted runner IP allowlists, do not want to run a registry, and do not want a self-hosted CI runner sitting on the box.

Boson keeps the deploy path server-side and webhook-driven:

```text
GitHub push webhook -> boson -> git fetch/reset -> docker compose up -d --build
```

1. **Point DNS at the server**, instead of allowlisting GitHub Actions runner IPs.
2. **Open ports 80 and 443**, instead of opening SSH to GitHub-hosted runners.
3. **Install Docker, git and the boson binary**, instead of putting SSH private keys in GitHub Secrets.
4. **Complete a GitHub App setup flow**, instead of running a Docker registry for deploys.
5. **Keep app secrets in server-side env files**, instead of storing registry pull credentials on the server.
6. **Let the production host build from the repo checkout**, instead of running a self-hosted CI runner on production.

General challenges boson is avoiding:

| Alternative | Cons |
|---|---|
| GitHub Actions + SSH | ❌ Requires SSH from GitHub-hosted runners into production<br>❌ GitHub-hosted runner IP ranges are broad and not recommended as internal allowlists<br>❌ Puts deploy credentials in GitHub Actions secrets<br>❌ Leaves the real deploy behavior in a remote shell script |
| GitHub Actions + private registry | ❌ Requires registry credentials in CI<br>❌ Requires pull credentials on the server for private images<br>❌ Adds image tagging, retention and rollback conventions<br>❌ Needs a second mechanism to tell the server to pull and restart |
| GitHub Actions + message bus + Watchtower | ❌ Requires a private registry and pull credentials<br>❌ Adds a message bus or queue<br>❌ Adds a long-running subscriber or polling process on the server<br>❌ Splits deploy behavior across CI, queue, subscriber, Watchtower and Docker |
| Self-hosted Actions runner | ❌ Runs a CI executor on or near production<br>❌ Self-hosted runners are not guaranteed to be clean between jobs<br>❌ Workflow security becomes production security<br>❌ Adds runner patching, isolation and lifecycle work |
| Dokku | ❌ Introduces a PaaS app model rather than plain Compose<br>❌ App layout and deploy behavior need to fit Dokku conventions<br>❌ More platform than a single Compose app needs |
| CapRover | ❌ Adds dashboard-managed platform state<br>❌ Git flow is less transparent than a server-side repo checkout<br>❌ More moving parts than a small webhook deployer |
| Coolify | ❌ CPU and resource heavy orchestration app<br>❌ More moving parts<br>❌ More platform state to debug when deploys fail |
| Kamal | ❌ Uses SSH orchestration to reach hosts<br>❌ Registry login and image distribution are part of the normal path<br>❌ More config than a single-server Compose checkout |
| Hand-rolled webhook | ❌ You own HMAC verification<br>❌ You own deploy locking and idempotency<br>❌ You own logs, retries, TLS, reverse proxy config and failure handling |

## Install

```bash
curl -fL https://github.com/pingfu/boson/releases/latest/download/boson-linux-x64 -o /tmp/boson
install -m 755 /tmp/boson /usr/local/bin/boson

boson init deploy.example.com        # boson's own hostname
```

`deploy.example.com` is boson's own hostname, resolving to this server. You use it to check the platform and to add projects. GitHub never connects to it, so a private name works too (`boson.enclave`), at the cost of a browser warning: Caddy issues that one itself, since a public CA won't.

Every project you add brings a second kind of hostname, its own public one, and that is where GitHub delivers its webhooks. Those need public A records pointing at this server, with ports 80 and 443 free on the host and reachable from the internet.

`init` verifies the software requirements (Docker with the compose v2 plugin, `git`, systemd, root), then installs the platform. TLS certificates are provisioned and renewed automatically, here and for each project hostname you add, and plain HTTP redirects to HTTPS. Confirm the install by opening `https://deploy.example.com/_boson/health`: a 200 proves DNS, reachability and TLS end to end.

## Prepare your project

Your repo needs two files at its root. A `_boson.yml` saying what to deploy where:

```yaml
version: 1

deployments:
  - branch: main
    hostname: example.org
    env: production
```

and a `docker-compose.yml` whose web-facing service publishes to loopback on `${BOSON_HOST_PORT}`:

```yaml
services:
  web:
    build: .
    ports:
      - "127.0.0.1:${BOSON_HOST_PORT}:8080"   # boson's port : your app's port
    env_file: ${BOSON_ENV_FILE}               # if your app takes env vars
```

That pair is the whole contract. One repo can serve several hostnames, deploy more than one branch, and give each pull request a hostname of its own: [BOSON_YML.md](BOSON_YML.md) covers the format.

`env: production` names a set of environment variables that lives on the server and never in the repo. You create the file after `boson add` and before the first deploy, at `/var/lib/boson/env/<org>/<name>/production`, and boson passes it to compose as `$BOSON_ENV_FILE`. Nothing is written into the checkout, so `git reset --hard` on each deploy cannot touch it, and a `--purge` cannot take it. Commit a template (`.env.example`) so the shape is in the repo and the values aren't. Different branches can name different sets, which is what keeps a preview branch off the production credentials.

boson picks the host port when you add the project, and sets `BOSON_HOST_PORT` for every `docker compose` it runs. Your repo names only the port your app listens on inside the container, `8080` here, so the same repo deploys to any boson server without carrying a number that's true on one machine. A deploy whose compose file publishes some other host port fails before it builds, and says which port boson expected.

Running compose by hand in a checkout needs both variables, with the port from `boson status`:

```bash
BOSON_HOST_PORT=30000 \
BOSON_ENV_FILE=/var/lib/boson/env/org/my-app/production \
  docker compose up -d
```

Four ports belong to the platform, and boson allocates from 30000-32767:

| Port | Listens on | Used by |
|---|---|---|
| 80 | every interface | Caddy, for plain HTTP |
| 443 | every interface | Caddy, for HTTPS: your sites are served here |
| 2019 | 127.0.0.1 | how boson tells Caddy which hostname goes to which container |
| 9000 | 127.0.0.1 | boson itself, which Caddy hands GitHub's push notifications and the setup pages |

Caddy proxies the public hostname to that loopback port. The whole hostname maps to your container: boson reserves one path on it, `/_boson/webhook/`, where GitHub posts.

## Add a project

```bash
boson add org/my-app --hostname my-app.example.com
```

Projects are referenced by repo name in every later command (`boson deploy org/my-app`). `--branch <name>` sets the tracked branch (default: `main`).

`add` allocates this project's host port, skipping any another project holds and any the host is already listening on. `boson status` shows which one it picked.

`add` prints a setup URL; open it in any browser (your own machine is fine): you create a GitHub App for the project, then install it on the repo. Boson then fetches the repo into `/srv/org/my-app/` and sets up the hostname's routing and certificate.

Ctrl-C stops the progress display, not the add: complete the browser steps and the project is added anyway (`boson status` shows it). Abandon the browser instead and nothing was saved; re-run the same command to start over.

`add` fetches the code but doesn't deploy, so you can set up secrets first. It reads the repo's `_boson.yml` and prints the environment sets it names, one path per set:

```bash
mkdir -p /var/lib/boson/env/org/my-app
cp /srv/org/my-app/.env.example /var/lib/boson/env/org/my-app/production
$EDITOR /var/lib/boson/env/org/my-app/production
```

Then `boson deploy org/my-app`: the first successful deploy switches on push-to-deploy. A deploy whose set doesn't exist fails naming the path, so a missing secret is never a half-started container.

## Deploy

**Every push to the tracked branch deploys automatically**: fetch, `docker compose up -d --build`, live. Pushes that land mid-deploy are remembered: the newest commit deploys when the running one finishes, and a burst of pushes costs at most one extra deploy.

`boson deploy org/my-app` deploys by hand: the first deploy after `add`, and any redeploy later. Safe to re-run. A deploy succeeds when `docker compose up` exits 0; add a compose `healthcheck` if you want a health gate.

Recover a bad deploy by pushing a fix, or a revert. Every deploy takes the branch tip, so the next push replaces whatever is running. A build that fails leaves the previous container in place, so a broken commit costs a failed deploy rather than an outage; a commit that builds and then misbehaves is live until the next push.

### Reading GitHub's webhook dashboard

In App → Advanced → Recent Deliveries: red means broken, green means fine.

| Status | Meaning |
|---|---|
| 202 | Push verified, deploy queued |
| 200 | Push verified, nothing to do (ping, untracked branch, or project awaiting first deploy) |
| 403 | Signature mismatch: the stored secret and GitHub disagree. An incident. |

A 202 means the deploy was queued; its outcome lives in `boson status` and the deploy log. Recover a failed delivery with GitHub's Redeliver button, or just run `boson deploy <org/name>`.

## Commands

```
boson init <admin-hostname>   # install the platform
boson add <org/name> ...      # add a project (then: boson deploy)
boson deploy <org/name>       # redeploy by hand
boson status                  # platform and projects: version, containers, last deploy
boson remove <org/name>       # tear down a project
boson uninstall               # remove the platform
```

Run every command as root: the CLI manages the platform's user, systemd unit and data directories, and the daemon's socket admits only root and the `boson` user.

Exit codes: `0` success · `1` user error · `2` runtime failure · `3` deploy already running · `99` internal bug (file an issue).

`https://deploy.example.com/_boson/health` returns the running version. When it doesn't answer, `systemctl status boson` says why.

`boson status` reports the daemon's version and the admin URL, then hostname, branch, host port, webhook, container states and last deploy for each project.

For everything else, the usual tools work: `docker logs` for container output, `journalctl -u boson` for the daemon, `/var/log/boson/deploys/` for per-deploy build output.

Re-running `boson init` rebuilds everything derived (routing, the systemd unit) from the database: the recovery move after restoring a backup or manual fiddling. Reboots take care of themselves.

## Upgrade

```bash
curl -fL https://github.com/pingfu/boson/releases/latest/download/boson-linux-x64 -o /tmp/boson
install -m 755 /tmp/boson /usr/local/bin/boson

systemctl restart boson
boson --version
```

Your sites keep serving throughout, and the restart waits for a deploy in flight to finish.

## Back up one file

`/var/lib/boson/boson.db` holds every project's GitHub App private key and webhook secret. GitHub issues these once and cannot re-issue them; lose the file and every project must be re-added by hand. Keep a copy off-host. A `cp` taken mid-write can be corrupt; snapshot with:

```bash
sqlite3 /var/lib/boson/boson.db "VACUUM INTO '/backup/boson.db'"
```

## Remove a project

```bash
boson remove <org/name>           # stop containers, drop routing, keep the record
boson remove <org/name> --purge   # also delete the record and /srv/<org>/<name>
```

Boson prints the App's settings URL so you can uninstall it on GitHub. The hostname and port are freed for reuse either way.

## Uninstall the platform

```bash
boson uninstall               # remove daemon, user, Caddy; keep all data
boson uninstall --purge       # destroy the data too (typed confirmation; offers a final DB backup)
```

The default keeps `boson.db` and all checkouts, so a later `init` brings everything back intact. Refuses to run while active projects exist (use `boson remove` first, or `--force`). The binary itself is yours to delete: `rm /usr/local/bin/boson`.
