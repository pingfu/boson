# platform-uninstall Specification

## Purpose

`boson uninstall [--force] [--purge]` walks back `boson init`: it stops the daemon, removes the unit, takes down Caddy and removes the `boson` user. `--purge` also destroys the state.

## Requirements

### Requirement: Mutual exclusion with init
The system SHALL hold `/var/lib/boson/locks/_platform.lock` exclusively for the duration of `boson uninstall`.

#### Scenario: Another init or uninstall is running
- WHEN the lock is held by another process
- THEN the command prints `another boson init/uninstall is running` and exits 3

### Requirement: Active project guard
The system SHALL refuse to uninstall while active projects exist, unless `--force` is given.

#### Scenario: Projects exist
- WHEN the database holds active projects and `--force` is absent
- THEN the command lists each repository, prints `remove them first (boson remove <org/name>) or pass --force` and exits 1
- AND nothing is torn down

#### Scenario: Schema cannot be read
- WHEN the database schema does not match the binary and `--force` is absent
- THEN the command prints the mismatch and `cannot verify projects; use --force to uninstall anyway`, and exits 2

#### Scenario: Forced
- WHEN `--force` is given
- THEN the uninstall proceeds whatever the database holds

[bug] Uninstall runs no `docker compose down` for project deployments. With `--force`, their containers keep running with no daemon and no Caddy, and `--purge` deletes the checkouts underneath them.

### Requirement: Purge confirmation
With `--purge`, the system SHALL ask the operator to type `destroy` before anything is torn down, and SHALL offer a final backup of the database when one exists.

#### Scenario: Confirmation not given
- WHEN the operator types anything other than `destroy`
- THEN the command prints `aborted; nothing was changed` and exits 1

#### Scenario: Backup requested
- WHEN the operator confirms and enters a backup path
- THEN the database is written with `VACUUM INTO` to a file in `/var/lib/boson/tmp`, set to mode `0600`, and moved to that path before teardown begins
- AND the file at that path is mode `0600`

#### Scenario: Backup cannot be written
- WHEN the path already exists, or writing or moving the backup fails
- THEN the command prints `could not write the backup to <path>: <reason>` and `aborted; nothing was changed`, and exits 2
- AND an existing file at that path is left as it was

#### Scenario: Backup skipped
- WHEN the operator confirms and enters an empty path
- THEN teardown begins with no backup

### Requirement: Teardown
The system SHALL, in order: run `systemctl disable --now boson`, delete the unit file and run `systemctl daemon-reload`; take the platform stack down with `docker compose -f - down`; and remove the `boson` user.

#### Scenario: Compose down fails
- WHEN taking the platform stack down exits non-zero
- THEN the command prints a warning and continues

[bug] The results of `systemctl` and `userdel` are discarded, and their `✓` lines print whether or not they worked.

### Requirement: State kept without purge
Without `--purge`, the system SHALL leave the database, `/var/lib/boson`, `/var/log/boson`, `/srv` and the Caddy volumes in place, so that a later `boson init` restores the platform with its projects.

#### Scenario: Uninstall then init
- WHEN the operator runs `boson uninstall --force` and later `boson init <control-hostname>`
- THEN every project and its GitHub App credentials are present after the init

### Requirement: Purge
With `--purge` and after confirmation and teardown, the system SHALL delete each active project's directory under `/srv`, delete `/var/lib/boson` and `/var/log/boson`, and remove the volumes `boson_caddy-data` and `boson_caddy-config`.

[bug] Checkouts of archived projects stay under `/srv`. When `--force` overrides a schema mismatch the project list is empty, so no checkout is deleted and no App settings URL is printed.

#### Scenario: A directory cannot be deleted
- WHEN deleting a directory throws
- THEN the command prints a warning naming the directory, or the repository for a checkout, and continues

#### Scenario: Projects existed
- WHEN the purge completes on a host that had active projects
- THEN the command prints each project's GitHub App settings URL for the operator to delete the App

#### Scenario: Binary
- WHEN any purge completes
- THEN the command prints `rm /usr/local/bin/boson` as the remaining step
