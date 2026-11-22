# status Specification

## Purpose

`boson status [--json]` reports the daemon and every active deployment: where it is served, what its containers are doing and how its last deploy ended.

## Requirements

### Requirement: Sources
The system SHALL read deployments and deploy history from the database, container state from `docker compose ps`, and the daemon's version and start time from `http://127.0.0.1:9000/_boson/health` with a 2 second timeout. The command works while the daemon is down.

#### Scenario: No database
- WHEN `/var/lib/boson/boson.db` does not exist
- THEN the command prints `no boson database found — run boson init first` and exits 1

#### Scenario: Schema mismatch
- WHEN the database schema does not match the binary
- THEN the command prints the mismatch and its fix, and exits 2

[bug] It exits 1 with the message inside a stack trace. See the exit code note in [project.md](../project.md).

### Requirement: Daemon line
The system SHALL print the daemon's state on the `boson version` line and the control hostname as `https://<control-hostname>` on the `admin url` line.

#### Scenario: Daemon running
- WHEN the health endpoint answers
- THEN the line reads `running <version>, up <uptime>, since <yyyy-MM-dd HH:mm> UTC`, with a commit suffix on the version cut to 7 characters

#### Scenario: Daemon down
- WHEN nothing answers on the health endpoint
- THEN the line reads `not responding — diagnose with: systemctl status boson`

#### Scenario: Binary replaced without a restart
- WHEN the daemon's version differs from the CLI binary's
- THEN the line adds the binary's version and `finish the upgrade with: systemctl restart boson`

### Requirement: Deployment table
The system SHALL print one row per active deployment, ordered by repository then branch, with these columns:

| Column | Content |
|---|---|
| repo | `org/name` |
| hostname | The hostname, followed by `(+N)` when it has N aliases |
| published port | The allocated loopback port |
| branch | The branch name |
| webhook | `active` or `inactive` |
| containers | Container states with counts, sorted by state, such as `restarting(1), running(2)`. `docker compose ps` is run without `--all`, so stopped containers are not counted |
| last deploy | `#<id> <status> <sha, 7 characters> <timestamp> (<age>)`, or `never` |

[bug] `BOSON_YML.md` says `boson status` reports why a deploy failed (invalid file, missing environment set). The table shows `failed` with no reason. The reason is in `--json` and the deploy log. A failure for a branch that has no deployment yet writes no deploy row and does not appear.

#### Scenario: No deployments
- WHEN no active deployment exists
- THEN the command prints `no deployments — add a project with: boson add <org/name>` and exits 0

#### Scenario: No containers
- WHEN `docker compose ps` lists nothing for the deployment
- THEN the containers column reads `none`

#### Scenario: Docker unavailable
- WHEN `docker compose ps` exits non-zero
- THEN the containers column reads `unavailable`

#### Scenario: Unreadable compose output
- WHEN the output of `docker compose ps` is neither a JSON array nor one JSON object per line
- THEN the containers column reads `unknown`

### Requirement: Dropped redeploy warning
The system SHALL warn for each deployment whose `deploy_pending` is set while its last deploy is not running.

#### Scenario: Pending flag on an idle deployment
- WHEN a deployment has `deploy_pending` set and its last deploy has finished
- THEN the command prints, after the table, `⚠ <org/name> <branch>: a push arrived that was never deployed. Recover with: boson deploy <org/name> --branch <branch>`
- AND with `--json` the deployment's `deployPendingIdle` is true

### Requirement: JSON output
With `--json`, the system SHALL print one indented JSON document with camelCase keys and exit 0:

| Key | Content |
|---|---|
| `adminHostname` | The control hostname, or null |
| `daemonRunning` | Whether the health endpoint answered |
| `daemonVersion`, `daemonStartedAt` | As reported by the daemon, or null |
| `cliVersion` | The version of the binary running the command |
| `deployments[]` | `repo`, `branch`, `hostname`, `aliases`, `port`, `webhookActive`, `containers`, `lastDeploy`, `deployPendingIdle` |
| `deployments[].lastDeploy` | `id`, `status`, `commitSha`, `startedAt`, `finishedAt`, `logPath`, `error`, or null when the deployment has never deployed |

#### Scenario: Failed last deploy
- WHEN a deployment's last deploy failed
- THEN its `lastDeploy.status` is `failed` and `lastDeploy.error` holds the reason
