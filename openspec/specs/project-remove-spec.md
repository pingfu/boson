# project-remove Specification

## Purpose

`boson remove <org/name> [--purge]` stops every deployment of a project and archives or deletes its record.

## Requirements

### Requirement: Stopping deployments
The system SHALL run `docker compose --project-name <name> down` for every active deployment of the project and archive each deployment, releasing its hostname, aliases and port.

#### Scenario: Project with two branches
- WHEN a project with deployments for `main` and `develop` is removed
- THEN both compose projects are taken down and both deployments archived

#### Scenario: Checkout is missing
- WHEN a deployment's checkout directory no longer exists
- THEN its containers are still taken down, because the compose project name identifies them

#### Scenario: Compose down fails
- WHEN `docker compose down` exits non-zero for a deployment
- THEN the failure is logged, the deployment is archived, and the removal continues

[bug] Removal takes no deployment lock. A deploy in flight runs `docker compose up` after the `down`, leaving containers running for a deployment that is archived or deleted.

### Requirement: Archive
Without `--purge`, the system SHALL archive the project row, blank its webhook secret and private key, keep its deploy history and leave `/srv/<org>/<name>` in place. An archived repository can be added again, which creates a new GitHub App.

#### Scenario: Remove then add
- WHEN a project is removed and `boson add` is run for the same repository
- THEN the add is accepted

[bug] An archived project cannot be purged afterwards: `boson remove --purge` answers `unknown project`. Its row and deploy history stay until the repository is added again and that project is purged.

### Requirement: Purge
With `--purge`, the system SHALL delete every project row for the repository, active and archived, which cascades to their deployments and deploy rows, and delete `/srv/<org>/<name>`. Deploy log files under `/var/log/boson/deploys/` remain.

#### Scenario: Checkout cannot be deleted
- WHEN deleting the directory throws
- THEN the failure is logged and the removal completes

[bug] The command then still prints `row, deploy history and checkout deleted`.

### Requirement: Routing
The system SHALL synchronise Caddy after the removal, so the project's hostnames stop being served.

#### Scenario: Hostname after removal
- WHEN a project is removed
- THEN the Caddy configuration holds no route for any of its hostnames or aliases

[bug] The synchronisation runs after the archive or purge is committed and its failure is not handled. With Caddy down, the command prints `remove failed (500)` and exits 2, the project is already gone, a re-run answers `unknown project`, and the routes stay until a later synchronisation.

### Requirement: Outcome
The system SHALL report what was removed and the GitHub App settings URL where the operator deletes the App.

#### Scenario: Unknown project
- WHEN no active project exists for the repository
- THEN the command prints `unknown project: <org/name>` and exits 1

#### Scenario: Removed without purge
- WHEN the removal succeeds without `--purge`
- THEN the command reports the record archived, the history kept and the checkout's path, prints the App settings URL and exits 0

#### Scenario: Environment sets
- WHEN a project is removed, with or without `--purge`
- THEN its files under `/var/lib/boson/env/<org>/<name>/` remain
