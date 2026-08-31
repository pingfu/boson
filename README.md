# boson

Push-to-deploy for a single Linux server. Boson clones your GitHub repos, runs each one with `docker compose up -d --build`, routes a public hostname to each with automatic TLS, and redeploys on every push.

Per project it takes three inputs: the repo, a public hostname, and the loopback port your compose file publishes.

## Install

```bash
curl -fL https://github.com/pingfu/boson/releases/latest/download/boson-linux-x64 \
  -o /usr/local/bin/boson && chmod +x /usr/local/bin/boson

boson init deploy.example.com
```

`init` verifies the requirements (Docker with the compose v2 plugin, `git`, systemd, root, ports 80/443 free, the admin hostname's A record, outbound HTTPS to Docker Hub and GitHub), then installs the platform. TLS certificates are provisioned and renewed automatically, here and for each project hostname you add. Done when `https://deploy.example.com/_boson/health` returns 200.

The admin hostname is the platform's own address, where GitHub delivers webhooks. It needs an A record pointing at the host, as does each project hostname.

## Prepare your project

Your repo needs a `docker-compose.yml` at its root whose web-facing service binds to loopback on a port you choose, unique per project on this host (80, 443, 2019 and 9000 are taken by the platform):

```yaml
services:
  web:
    build: .
    ports:
      - "127.0.0.1:8080:8080"
    env_file: .env                # if your app takes env vars
```

Caddy proxies the public hostname to that loopback port. If your app takes env vars, reference `.env`, commit a template (`.env.example`) and gitignore the real file: the admin creates `.env` on the server after `boson add`.

## Add a project

```bash
boson add org/my-app --hostname my-app.example.com --upstream-port 8080
```

Projects are referenced by repo name in every later command (`boson deploy org/my-app`). `--branch <name>` sets the tracked branch (default: `main`).

`add` prints a setup URL; open it in any browser (your own machine is fine): you create a GitHub App for the project, then install it on the repo. Boson then fetches the repo into `/srv/org/my-app/` and sets up the hostname's routing and certificate.

Ctrl-C stops the progress display, not the add: complete the browser steps and the project is added anyway (`boson list` shows it). Abandon the browser instead and nothing was saved; re-run the same command to start over.

`add` fetches the code but doesn't deploy, so you can set up secrets first: if your compose needs env vars, create `/srv/org/my-app/.env` (start from the repo's `.env.example`). Then run `boson deploy org/my-app`: the first successful deploy switches on push-to-deploy.

## Deploy

**Every push to the tracked branch deploys automatically**: fetch, `docker compose up -d --build`, live. Pushes that land mid-deploy are remembered: the newest commit deploys when the running one finishes, and a burst of pushes costs at most one extra deploy.

`boson deploy org/my-app` deploys by hand: the first deploy after `add`, and any redeploy later. Safe to re-run. A deploy succeeds when `docker compose up` exits 0; add a compose `healthcheck` if you want a health gate.

### Reading GitHub's webhook dashboard

In App → Advanced → Recent Deliveries: red means broken, green means fine.

| Status | Meaning |
|---|---|
| 202 | Push verified, deploy queued |
| 200 | Push verified, nothing to do (ping, untracked branch, or project awaiting first deploy) |
| 403 | Signature mismatch: the stored secret and GitHub disagree. An incident. |

A 202 means the deploy was queued; its outcome lives in `boson list` and the deploy log. Recover a failed delivery with GitHub's Redeliver button, or just run `boson deploy <org/name>`.

## Commands

```
boson init <admin-hostname>   # install the platform
boson add <org/name> ...      # add a project (then: boson deploy)
boson deploy <org/name>       # redeploy by hand
boson list                    # all projects: containers, last deploy
boson remove <org/name>       # tear down a project
boson uninstall               # remove the platform
```

Run every command as root: the CLI manages root-owned paths and talks to the daemon over a root-only socket.

Exit codes: `0` success · `1` user error · `2` runtime failure · `3` deploy already running · `99` internal bug (file an issue).

For everything else, the usual tools work: `docker logs` for container output, `journalctl -u boson` for the daemon, `/var/log/boson/deploys/` for per-deploy build output.

Upgrading boson: re-run the install `curl`, then `systemctl restart boson`.

Re-running `boson init` rebuilds everything derived (routing, the systemd unit) from the database: the recovery move after restoring a backup or manual fiddling. Reboots take care of themselves.

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
