# Using boson: a guide for AI agents

## What boson is

Boson is push-to-deploy for a single Linux server. It runs on the production host. When a push lands on a branch that the repo's `_boson.yml` declares, boson fetches that branch's tip into `/srv/<org>/<name>/<branch-slug>/`, runs `docker compose up -d --build` there, and routes the branch's public hostname, with a Let's Encrypt certificate, to the container through Caddy. The repo carries a Compose file and a `_boson.yml`; the server holds the secrets and the data.

## Use boson for

- **A web app or API on one server.** A repo with a Compose file becomes a live HTTPS site, redeployed by `git push`.
- **A whole Compose stack behind one hostname.** The app with its database, cache, workers and sidecars comes up as one deployment. The stack can be as large as the server can run: one service publishes the HTTP port and the rest talk over the compose network.
- **Several apps sharing one box.** Each project gets its own hostname, certificate, loopback port and containers.
- **Branch previews.** A pattern entry such as `branch: "*"` gives every pushed branch its own hostname and containers, stopped once `expire_after` has passed since its last successful deploy.
- **Staging beside production.** Two branches of one repo, for example `main` and `develop`, run as two deployments with separate hostnames, secrets and data.
- **Running someone else's published image.** A Compose file naming an image, with no build, gets a hostname and TLS the same way.
- **Deploys with no production credentials in CI.** The server pulls from GitHub. Nothing outside it holds a key to it.

Reach for boson whenever the task is "put this repo on the internet on this server".

This file covers making a repo deployable and operating boson on the server. [README.md](README.md) and [BOSON_YML.md](BOSON_YML.md) hold the full reference.

## Make a repo deployable

Two files at the repo root.

`_boson.yml` maps branches to hostnames:

```yaml
version: 1

deployments:
  - branch: main
    hostname: example.org
    env: production        # optional: names a secrets file on the server
```

`docker-compose.yml` runs the app:

```yaml
services:
  web:
    build: .
    image: my-app:${BOSON_COMMIT}
    ports:
      - "127.0.0.1:${BOSON_PORT}:8080"
    env_file: ${BOSON_ENV_FILE}
    volumes:
      - /var/lib/my-app/${BOSON_BRANCH_SLUG}:/data
    restart: unless-stopped
```

Boson sets four variables for every `docker compose` it runs:

| Variable | Value | Example |
|---|---|---|
| `BOSON_PORT` | The loopback port boson allocated to this branch, from 30000-32767 | `30000` |
| `BOSON_BRANCH_SLUG` | The branch name as a DNS label: lowercased, anything outside `a-z0-9-` replaced with `-`, plus a short hash when that changed the name | `main`, `feature-add-search-<hash>` |
| `BOSON_COMMIT` | The commit SHA this deploy fetched | `44f9610…` |
| `BOSON_ENV_FILE` | The path of the env set this branch's entry names | `/var/lib/boson/env/org/my-app/production` |

Rules the compose file has to meet:

- **Port.** The web-facing service publishes `127.0.0.1:${BOSON_PORT}:<container port>`, where the container port is the one the app listens on (`8080` above). Caddy dials the loopback port. That is the only published port in the whole file: boson reads `docker compose config` and fails the deploy before the build if no service publishes a port, if any service publishes a different host port, or if any binding is not on loopback.
- **Other services.** A stack can hold any number of them (a database, a cache, workers, sidecars). They reach each other by service name over the compose network and publish nothing on the host.
- **One HTTP port is the whole public surface.** A service that needs a second port open to the internet, or speaks something other than HTTP to it (SMTP, a game server, a TURN relay, raw TCP or UDP), fails the port check. Run that service outside boson, with its own Compose file, and keep the HTTP part of the project in boson.
- **State.** Each deploy runs `git reset --hard` in the checkout, which puts every tracked file back as committed, and a purge deletes the checkout whole. Data goes in a host path or a named volume with `${BOSON_BRANCH_SLUG}` in its name (`/var/lib/my-app/main`, `my-app-main`), so `main` and `develop` never share a database.
- **Secrets.** `env: production` in `_boson.yml` means the file `/var/lib/boson/env/<org>/<name>/production` on the server, owned by `boson:boson` with mode `0600`, read through `env_file: ${BOSON_ENV_FILE}`. A named set with no file fails the deploy before the build and names the path to create. Leave `env` out when the app needs no environment file, and drop the `env_file` line with it.
- **Image.** `build:` with `image: my-app:${BOSON_COMMIT}`, or a published `image:` with no build. For a moving tag such as `latest`, add `pull_policy: always` so each deploy fetches the current image.
- **Health.** A deploy succeeds when `docker compose up` exits 0. Add a compose `healthcheck` for a health gate.

Rules for `_boson.yml`:

- **Each hostname has a public A record first.** It resolves to the server's public address, with ports 80 and 443 reachable from the internet, before `boson add`. Let's Encrypt validates there, and GitHub posts the push webhook to `https://<hostname>/_boson/webhook/<org>/<name>`.
- **A hostname is a plain DNS name.** Letters, digits, `-` and `.`, for `hostname` and every alias. A wildcard (`*.example.org`), a port, a URL or a trailing dot fails the deploy. `{branch}` is allowed in a pattern entry's `hostname`.
- **Name at least one branch outright.** The webhook address is the hostname of the default branch's deployment, or of the first named branch in alphabetical order when the default has none. A file holding only pattern entries has no deployment at add time, so the webhook stays on boson's admin hostname, and pushes are delivered only when GitHub can reach that name.
- **A pattern entry (`branch: "*"`, `branch: "feature/*"`) creates a deployment on the first push of each matching branch**, with empty state and a hostname from its template (`"{branch}.example.org"`). Give it a different `env` set from production, or every branch anyone pushes gets production's credentials.
- **Entries match top to bottom and the first match wins.** List `main` above `"*"`.

A config file the app reads at runtime, as opposed to environment variables, belongs in the app's data path on the server (`/var/lib/my-app/main/config.yaml`), created before the first deploy.

## Operate boson

Run every command as root on the server. `<org/name>` is the GitHub repository, for example `acme/site`.

| Command | Does |
|---|---|
| `boson init <admin-hostname>` | Installs boson on the server: the daemon, Caddy and the admin hostname. Run once, and again to repair an install |
| `boson status` | Lists every deployment: repo, hostname, published port, branch, webhook state, container state, last deploy |
| `boson add <org/name>` | Registers a repo: creates its GitHub App, clones the default branch, creates a deployment for each branch `_boson.yml` names outright |
| `boson deploy <org/name>` | Deploys the tip of the project's one branch now and streams the build log. A project with several branches needs `--branch <name>` |
| `boson remove <org/name>` | Stops the containers of every branch, drops their routes and erases the project's GitHub App key. `--purge` also deletes the project's record and `/srv/<org>/<name>` |
| `boson uninstall` | Removes the boson daemon, its user and Caddy from the server |

The admin hostname is the callback address for GitHub App setup (not the webhook address GitHub posts pushes to), given to `boson init` and shown by `boson status` as `Admin url`. It is used each time a project is added: `boson add <org/name>` prints a URL on it, the operator opens that URL in a browser, the page hands them to GitHub to create the App, and GitHub redirects the browser back to it once the App is created and again once it is installed. The callbacks are browser redirects, so the operator's browser is what has to reach it, and GitHub's servers do not. Between adds nothing has to reach it: deploys, webhooks and the sites themselves run without it. The exception is a project whose webhook is still on it: a repo that declares pattern entries alone, covered in the last point below, or one where `boson add` warned that it could not move the webhook address.

It also serves `https://<admin-hostname>/_boson/health`, which answers 200 with the daemon's version and start time. That page is for a person: `boson init` prints the URL so the operator can open it and see that DNS and TLS work. No part of boson reads it there. `boson init` and `boson status` ask the daemon directly on `127.0.0.1:9000`, and on the server `boson status` reports the same thing.

The webhook address is a different one. GitHub's servers post each push to the project's own public hostname, at `https://<hostname>/_boson/webhook/<org>/<name>`, which is why that hostname has to be reachable from the internet and the admin hostname does not.

Choosing one:

- **It resolves to the server from the operator's browser.** That is the whole reachability requirement, so a private name works (`boson.enclave`, a VPN or internal DNS name). Caddy issues a private name's certificate itself, and the browser warns about it until the operator trusts it.
- **A public name gets a Let's Encrypt certificate** (`deploy.example.com`), and puts the setup and health pages on the internet.
- **No project uses it.** A `_boson.yml` that declares the admin hostname as a hostname or an alias fails its deploy.
- **It is public when a repo declares pattern entries alone.** That repo's webhook stays on the admin hostname, so GitHub has to reach it there.

The sequence for a new project:

1. **`boson add <org/name>` needs a person.** It prints a URL of the form `https://<admin-hostname>/_boson/setup-app/start?state=…`. The operator opens it in a browser that can reach the admin hostname, creates the GitHub App, and installs it on the repo. The URL is good for 15 minutes. Ask the operator to do this step and wait for them.
2. **Create each env set `add` lists.** For `env: production` in `acme/site`:

   ```bash
   mkdir -p /var/lib/boson/env/acme/site
   cp /srv/acme/site/main/.env.example /var/lib/boson/env/acme/site/production
   chown -R boson:boson /var/lib/boson/env
   chmod 600 /var/lib/boson/env/acme/site/production
   ```

3. **`boson deploy <org/name>`.** The first successful deploy of a branch switches on push-to-deploy for that branch.

Things that go wrong in step 1:

- **The App cannot read the repo.** A private App installs only on the account that owns it. Boson asks `api.github.com/users/<org>` whether the repo's owner is an organisation and, when it is, creates the App under that organisation. If GitHub refuses the lookup, pass `--owner org` or `--owner user`.
- **`add` saved the App and then stopped**, because the clone failed or `_boson.yml` did not parse. Fix the cause and run `boson deploy <org/name>`, which fetches again, creates the deployments and deploys. `boson add` refuses a repo it already holds.

Where to look:

- `boson status` for what is deployed and whether the last deploy succeeded.
- `/var/log/boson/deploys/<deploy-id>.log` for one deploy's build output. `boson status` shows the id as `#<n>`.
- `journalctl -u boson` for the daemon.
- `docker logs <org>-<name>-<branch-slug>-<service>-1` for the app, for example `acme-site-main-web-1`.
- Exit codes: `0` success, `1` user error or an error boson did not expect (it prints a stack trace), `2` runtime failure, `3` the daemon refused the deploy as it stands. Read the message with a `3`: it is either a deploy of that branch already running, or a project with several branches that needs `--branch <name>`. `boson init` and `boson uninstall` also exit `3` when the other is running.

## Boundaries

- **Caddy belongs to boson.** Boson rebuilds Caddy's whole config from its database on every sync, so a route added through Caddy's admin API or a Caddyfile is lost. A hostname gets a route by being declared in a project's `_boson.yml`.
- **Publish on `127.0.0.1`.** A `0.0.0.0` bind puts the app's plain HTTP port on the public internet on a server with no firewall.
- **Push access to a deployed branch is root on the server.** boson hands each repo's compose file, as written, to the server's Docker. A standard Docker install runs its daemon as root, so a compose file can mount the host's filesystem or run privileged. Deploy a repo, and match a branch with a pattern entry, when everyone who can push there is trusted with the host.
- **Run the app as an ordinary user.** Set `user:` on each service, or `USER` in its Dockerfile, so the process inside the container is not root. A compromised app is then an unprivileged user in its container.
- **Secrets stay on the server.** `_boson.yml` and the repo carry the names of env sets, never their values.
- **Ask before `boson remove`, with or without `--purge`, and before `boson uninstall --purge`.** GitHub issues an App's private key once. Removing a project erases the key from `boson.db`, and adding it back means the operator deleting the App on GitHub and creating a new one in the browser. `boson uninstall` without `--purge` keeps the database and every key.
