# platform-install Specification

## Purpose

`boson init <control-hostname>` installs the platform on a host: the `boson` system user, the directories, the daemon under systemd, and Caddy. Re-running it rebuilds everything derived from the database.

## Requirements

### Requirement: Control hostname validation
The system SHALL trim the control hostname, remove a trailing `.`, and lowercase it, and SHALL refuse a value that is empty or contains no `.`.

#### Scenario: Hostname without a dot
- **WHEN** the operator runs `boson init localhost`
- **THEN** the command prints `invalid control hostname: localhost` and exits 1
- **AND** nothing on the host is changed

### Requirement: Preflight
The system SHALL verify the host's prerequisites before changing anything, SHALL run every check before reporting, and SHALL install none of them.

The checks are:

| Check | Passes when |
|---|---|
| Docker engine | `docker info` exits 0 |
| Compose | `docker compose version --short` reports major version 2 or higher |
| Privilege | The process is root |
| Git | `git --version` exits 0 |
| Init system | `/run/systemd/system` exists and `systemctl` is on `PATH` |

#### Scenario: Several prerequisites missing
- **WHEN** Docker is not running and git is not installed
- **THEN** the command lists both problems, each with what needs it and the command that fixes it
- **AND** prints `No changes were made.` and exits 1

### Requirement: Mutual exclusion with uninstall
The system SHALL hold `/var/lib/boson/locks/_platform.lock` exclusively for the duration of `boson init`.

#### Scenario: Another init or uninstall is running
- **WHEN** the lock is held by another process
- **THEN** the command prints `another boson init/uninstall is running` and exits 3

### Requirement: System user
The system SHALL create the system user `boson` when it does not exist, with no home directory created, home set to `/var/lib/boson` and shell `/usr/sbin/nologin`, and SHALL add it to the `docker` group.

#### Scenario: User already exists
- **WHEN** `id -u boson` succeeds
- **THEN** the user is not recreated
- **AND** it is added to the `docker` group

#### Scenario: User creation fails
- **WHEN** `useradd` or `usermod` exits non-zero
- **THEN** the command prints the tool's error and exits 2

### Requirement: Directories
The system SHALL create `/var/lib/boson`, `/var/lib/boson/locks`, `/var/lib/boson/tmp`, `/var/log/boson`, `/var/log/boson/deploys` and `/srv`, and SHALL set `/var/lib/boson`, `/var/log/boson` and `/srv` to owner `boson:boson` recursively and mode `0700`.

#### Scenario: Directories exist from an earlier install
- **WHEN** the directories already exist and are owned by a different uid
- **THEN** they are re-owned to `boson:boson` and their contents are kept

### Requirement: Binary installation
The system SHALL copy the running binary to `/usr/local/bin/boson` with mode `0755` when it is running from any other path, by writing `/usr/local/bin/boson.new` and renaming it over the target.

#### Scenario: Run from a download directory
- **WHEN** the operator runs `./boson init boson.example.org` from `/root`
- **THEN** `/usr/local/bin/boson` holds the same binary
- **AND** a running daemon executing the old binary does not block the replacement

#### Scenario: Copy fails
- **WHEN** the copy or rename throws
- **THEN** the command prints `failed to install the binary to /usr/local/bin/boson: <reason>` and exits 2

### Requirement: systemd unit
The system SHALL write `/etc/systemd/system/boson.service` when its content differs from the unit embedded in the binary, then run `systemctl daemon-reload`, `systemctl enable boson`, `systemctl reset-failed boson` and `systemctl restart boson` in that order. A failure of any step other than `reset-failed` SHALL stop the command with exit 2.

The unit runs `/usr/local/bin/boson serve` as `boson:boson` with working directory `/var/lib/boson`, `Restart=always`, `RestartSec=2`, `NoNewPrivileges=true`, `TimeoutStopSec=600` and `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/boson/.net`.

#### Scenario: Unit edited by hand
- **WHEN** the unit file on disk differs from the embedded unit
- **THEN** init overwrites it

#### Scenario: Unit wedged by the start limit
- **WHEN** systemd refuses to start the unit after repeated failures
- **THEN** `reset-failed` clears the state and the restart proceeds

#### Scenario: daemon-reload fails
- **WHEN** `systemctl daemon-reload` exits non-zero
- **THEN** the command prints `systemctl failed: <stderr>` and exits 2 without enabling or restarting the unit

### Requirement: Daemon health wait
The system SHALL poll `http://127.0.0.1:9000/_boson/health` every 500 ms for up to 10 seconds after the restart and continue once it answers with a success status.

#### Scenario: Daemon never becomes healthy
- **WHEN** no success response arrives within 10 seconds
- **THEN** the command prints the HTTP status and body it last received, or the connection error
- **AND** prints `diagnose with: journalctl -u boson` and exits 2

### Requirement: Platform rows
The system SHALL check the database schema against the binary, then store `admin_hostname`, `installed_at` (UTC, ISO 8601) and `binary_version` in the `platform` table.

#### Scenario: Re-run with a different hostname
- **WHEN** init is re-run with another control hostname
- **THEN** `admin_hostname` is replaced and the Caddy configuration pushed afterwards uses the new name

### Requirement: Platform stack
The system SHALL start the platform stack with `docker compose -f - up -d`, piping the embedded compose file over stdin.

The stack is compose project `boson` with one service: `caddy:2-alpine`, container name `boson-caddy`, `restart: unless-stopped`, `network_mode: host`, command `caddy run --resume`, and the named volumes `caddy-data` at `/data` and `caddy-config` at `/config`.

#### Scenario: Compose up fails
- **WHEN** `docker compose up` exits non-zero
- **THEN** the command prints `docker compose up failed for the platform stack: <stderr>` and exits 2

### Requirement: Caddy bootstrap
The system SHALL wait up to 10 seconds for Caddy's admin API, then load the full configuration built from the control hostname and every active deployment, as specified in [routing](routing-spec.md).

#### Scenario: Recovery on a host with projects
- **WHEN** init is re-run on a host whose database holds active deployments
- **THEN** the configuration it loads routes every one of them

#### Scenario: Admin API unreachable
- **WHEN** the admin API does not answer within 10 seconds
- **THEN** the command prints `caddy admin API not reachable within 10s` with `docker logs boson-caddy` as the diagnostic and exits 2

### Requirement: Hand-off
The system SHALL finish by printing `https://<control-hostname>/_boson/health` as the URL that proves DNS, reachability and TLS, and exit 0.

#### Scenario: Successful install
- **WHEN** every step succeeds
- **THEN** the command prints the health URL and exits 0
