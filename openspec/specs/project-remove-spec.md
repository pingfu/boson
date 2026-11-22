# project-remove Specification

## Purpose

`boson remove <org/name> [--purge]` stops every deployment of a project and archives or deletes its record.

## Requirements

### Requirement: Stopping deployments
The system SHALL run `docker compose --project-name <name> down` for every active deployment of the project and archive each deployment, releasing its hostname, aliases and port.

#### Scenario: Project with two branches
- **WHEN** a project with deployments for `main` and `develop` is removed
- **THEN** both compose projects are taken down and both deployments archived

#### Scenario: Checkout is missing
- **WHEN** a deployment's checkout directory no longer exists
- **THEN** its containers are still taken down, because the compose project name identifies them

#### Scenario: Compose down fails
- **WHEN** `docker compose down` exits non-zero for a deployment
- **THEN** the failure is logged and the removal continues

### Requirement: Archive
Without `--purge`, the system SHALL archive the project row, keep its deploy history and leave `/srv/<org>/<name>` in place. An archived repository can be added again.

#### Scenario: Remove then add
- **WHEN** a project is removed and `boson add` is run for the same repository
- **THEN** the add is accepted

### Requirement: Purge
With `--purge`, the system SHALL delete the project row, which cascades to its deployments and deploy history, and delete `/srv/<org>/<name>`.

#### Scenario: Checkout cannot be deleted
- **WHEN** deleting the directory throws
- **THEN** the failure is logged and the removal completes

### Requirement: Routing
The system SHALL synchronise Caddy after the removal, so the project's hostnames stop being served.

#### Scenario: Hostname after removal
- **WHEN** a project is removed
- **THEN** the Caddy configuration holds no route for any of its hostnames or aliases

### Requirement: Outcome
The system SHALL report what was removed and the GitHub App settings URL where the operator deletes the App.

#### Scenario: Unknown project
- **WHEN** no active project exists for the repository
- **THEN** the command prints `unknown project: <org/name>` and exits 1

#### Scenario: Removed without purge
- **WHEN** the removal succeeds without `--purge`
- **THEN** the command reports the record archived, the history kept and the checkout's path, prints the App settings URL and exits 0

#### Scenario: Environment sets
- **WHEN** a project is removed, with or without `--purge`
- **THEN** its files under `/var/lib/boson/env/<org>/<name>/` remain
