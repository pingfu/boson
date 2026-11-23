# daemon Specification

## Purpose

`boson serve` is the resident process, started by systemd. It runs every deploy, serves the webhook and App setup endpoints behind Caddy, and answers the CLI over a unix socket.

## Requirements

### Requirement: Startup
The system SHALL, on start and in this order: create its data and log directories, migrate the database to the binary's schema, mark interrupted deploys as failed, start the expiry sweep, and start both listeners.

#### Scenario: Start after an upgrade
- WHEN the daemon starts with a binary that carries newer migrations
- THEN the migrations are applied before any listener accepts a request

#### Scenario: Database newer than the binary
- WHEN the database schema version is higher than the binary's
- THEN the daemon exits 1 with a message telling the operator to upgrade the binary, and systemd restarts it every 2 seconds until that is done

### Requirement: Public listener
The system SHALL listen on port 9000 of the loopback interface, IPv4 and IPv6, and serve:

| Route | Purpose |
|---|---|
| `GET /_boson/health` | `{"status":"ok","version":"<version>","startedAt":"<UTC timestamp>"}` |
| `POST /_boson/webhook/{org}/{name}` | See [webhook-deploy](webhook-deploy-spec.md) |
| `GET /_boson/setup-app/start` | See [github-app](github-app-spec.md) |
| `GET /_boson/setup-app/callback` | See [github-app](github-app-spec.md) |
| `GET /_boson/setup-app/installed` | See [github-app](github-app-spec.md) |

`--port` changes the port for a foreground run on a workstation. Caddy's routes target 9000.

#### Scenario: Health check
- WHEN a client requests `GET /_boson/health`
- THEN the response is 200 with the daemon's version and start time

#### Scenario: Setup request for a dead token
- WHEN a setup route is requested with a token that has no live setup
- THEN the response is 404 and the daemon logs the route, the first 8 characters of the token and the reason

### Requirement: RPC listener
The system SHALL listen on the unix socket `/var/lib/boson/boson.sock` and serve the CLI:

| Route | Used by | Answers |
|---|---|---|
| `POST /add` | `boson add` | 200 with the setup URL, token and warnings. 409 when refused |
| `GET /add/status/{token}` | `boson add` | 200 with phase, error, warnings and environment sets. 404 for an unknown token |
| `POST /deploy/{org}/{name}?branch=` | `boson deploy` | 202 with the deploy id, or with `declaring: true`. 404 or 409 when refused |
| `GET /declare/status/{org}/{name}` | `boson deploy` | 200 with phase `declaring`, `done` or `failed`. 404 when no declaration is known |
| `POST /remove/{org}/{name}?purge=` | `boson remove` | 200 with the App settings URL. 404 for an unknown project |

Bodies are JSON with camelCase keys. A refusal carries `{"error":"<reason>"}`, and the CLI prints that reason as it stands. The 404s from the two status routes have no body.

#### Scenario: Refused deploy
- WHEN a deploy request is refused
- THEN the response body states the reason, and `boson deploy` prints it and exits 1 for a 404, 3 for a 409 and 2 otherwise, which includes the 422 for a deploy that stopped before it had a row

### Requirement: Socket access
The system SHALL delete a socket file left by an earlier process before binding, and set the socket's mode to `0660` once bound. Permission to open the socket is the RPC's authentication.

#### Scenario: Restart after a kill
- WHEN the daemon was killed and its socket file remains
- THEN the next start binds successfully

### Requirement: Daemon unreachable
The CLI SHALL report a daemon it cannot reach over the socket with `boson daemon unreachable — check: systemctl status boson / journalctl -u boson` and exit 2. Each RPC request times out after 30 seconds.

#### Scenario: Daemon stopped
- WHEN `boson deploy` is run while the daemon is stopped
- THEN the command prints the unreachable message and exits 2

[bug] A request that reaches the daemon and then times out raises a cancellation the client does not convert, so the command exits 1 and prints nothing while the daemon carries on.

### Requirement: Requests return before long work
The system SHALL answer webhook, deploy and add requests before the work they start completes. Deploys, declarations and setup finalisation run on background tasks, and the CLI follows them by polling.

[bug] Two requests do long work before answering, inside the client's 30 second timeout. A deploy request answers once the fetch and `_boson.yml` steps are done and the deploy row exists. A remove request runs `docker compose down` for every deployment and the Caddy synchronisation first.

#### Scenario: Slow first clone
- WHEN a declaration clones a large repository
- THEN no RPC request stays open for the length of the clone

### Requirement: Logging
The system SHALL write its log to stdout, which systemd captures in the journal, and as JSON lines to `/var/log/boson/boson.log`, rolled at 10 MB with 5 files kept.

#### Scenario: Reading why a deployment expired
- WHEN the operator runs `journalctl -u boson`
- THEN the expiry of each deployment is in the output

### Requirement: Graceful shutdown
The system SHALL, on SIGTERM or SIGINT, stop both listeners, wait up to 590 seconds for running deploys to finish, and exit 0. The unit's `TimeoutStopSec` is 600 and must stay above that wait.

#### Scenario: Restart during a build
- WHEN `systemctl restart boson` is run while a deploy is building
- THEN the build finishes and its outcome is recorded before the daemon exits

[bug] The unit sets no `KillMode`, and the server reports `KillMode=control-group`, under which systemd signals every process in the unit, including the `docker compose up --build` child. Whether a build survives that was not tested. The wait also covers deploys alone: expiry teardowns, declarations and setup finalisation are cut off at exit.

#### Scenario: Deploy outlasts the wait
- WHEN a deploy is still running after 590 seconds
- THEN the daemon exits, and the deploy's row is marked failed with `daemon restart` on the next start
