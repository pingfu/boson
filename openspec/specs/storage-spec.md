# storage Specification

## Purpose

boson keeps its state in one SQLite database, `/var/lib/boson/boson.db`: platform settings, projects with their GitHub App credentials, deployments and deploy history.

## Requirements

### Requirement: Connections
The system SHALL open every connection with `journal_mode = WAL`, `foreign_keys = ON` and `busy_timeout = 5000`. The daemon and CLI processes use the database concurrently.

#### Scenario: CLI reads during a deploy
- WHEN `boson status` runs while the daemon writes a deploy row
- THEN both complete

### Requirement: Schema
The system SHALL store:

| Table | One row per | Key columns |
|---|---|---|
| `schema_version` | Applied migration | `version`, `applied_at` |
| `platform` | Setting | `key`, `value`. Keys: `admin_hostname`, `installed_at`, `binary_version` |
| `projects` | Repository | `repo`, `github_app_id`, `github_app_slug`, `github_installation_id`, `github_webhook_secret`, `github_app_pem`, `github_app_org`, `archived_at` |
| `deployments` | Branch of a project | `project_id`, `branch`, `branch_slug`, `hostname`, `port`, `env_set`, `expire_after`, `webhook_active`, `deploy_pending`, `archived_at` |
| `deployment_names` | Hostname or alias of an active deployment | `deployment_id`, `name`, `kind` (`primary` or `alias`) |
| `deploys` | Deploy | `deployment_id`, `trigger`, `commit_sha`, `started_at`, `finished_at`, `status`, `log_path`, `error` |

`repo` is stored lowercase. `hostname` and `name` compare case-insensitively. Timestamps are UTC text as written by SQLite's `datetime('now')`. `trigger` is `manual` or `webhook`. `status` is `running`, `succeeded` or `failed`.

#### Scenario: Reading the database by hand
- WHEN the operator queries `deploys` with the `sqlite3` tool
- THEN `status` and `trigger` read as lowercase words

### Requirement: Whole project rows
The system SHALL require every GitHub App credential column on `projects` except `github_app_org` to be non-null. `github_app_org` is null for an App owned by a personal account.

#### Scenario: Insert without a private key
- WHEN a project insert omits `github_app_pem`
- THEN the insert fails

### Requirement: Uniqueness among active rows
The system SHALL enforce, among rows whose `archived_at` is null: one project per `repo`; one deployment per project and `branch`; one per project and `branch_slug`; one per `hostname`; one per `port`. The system SHALL enforce one `deployment_names` row per name.

#### Scenario: Same repository added twice
- WHEN a project is inserted for a repository that has an active project
- THEN the insert is refused with `repo is already added: <org/name>`

#### Scenario: Colliding deployment
- WHEN a deployment is inserted with a hostname, alias, port or branch an active deployment holds
- THEN the insert is rolled back and refused with a message naming the hostname, port and branch

### Requirement: Archiving
The system SHALL archive a project or deployment by setting `archived_at`, keeping the row and its deploy history. Archiving a deployment deletes its `deployment_names` rows.

#### Scenario: Archived deployment's names
- WHEN a deployment is archived
- THEN its hostname, aliases, port, branch and slug can be held by a new deployment

### Requirement: Purging
The system SHALL delete a purged project's row, and with it, by cascade, its deployments, their names and their deploys.

#### Scenario: Purge
- WHEN a project is removed with `--purge`
- THEN no row in any table refers to it

### Requirement: Migrations
The system SHALL embed migrations as `NNNN_<name>.sql` and apply, at daemon start, each whose number is above the database's version, in order, each in its own transaction together with its `schema_version` row. The CLI does not migrate.

[bug] `0002_app_org.sql` adds `github_app_org` as a second migration. `AGENTS.md` says a schema change rewrites `0001_init.sql`.

#### Scenario: Fresh database
- WHEN the daemon starts with no database file
- THEN the file is created and every migration applied

#### Scenario: Migration fails
- WHEN a migration's SQL fails
- THEN its transaction rolls back and the daemon does not start

### Requirement: Schema compatibility
Every CLI command that reads the database SHALL compare its schema version with the binary's before doing so, and exit 2 on a difference with the fix in the message.

#### Scenario: Binary upgraded, daemon not restarted
- WHEN the database is behind the binary
- THEN the message is `database schema (v<n>) is behind this binary (v<m>); run: systemctl restart boson`

#### Scenario: Binary downgraded
- WHEN the database is ahead of the binary
- THEN the message is `database schema (v<n>) is newer than this binary (v<m>); upgrade the binary`

### Requirement: File ownership
A CLI command run as root that opens the database SHALL set the owner of `boson.db`, `boson.db-wal` and `boson.db-shm` to `boson:boson` afterwards, so the daemon can write to them.

#### Scenario: Root runs status
- WHEN root runs `boson status` and SQLite creates the `-wal` and `-shm` files
- THEN the files are owned by `boson:boson` when the command ends

### Requirement: Credentials at rest
GitHub App private keys and webhook secrets are stored as plain text. `/var/lib/boson` is mode `0700` and owned by `boson`. A backup of the database contains every project's credentials.

[bug] The comment in `0001_init.sql` says the database file is `0600`. No code sets the file's mode. The `0700` directory is what protects it.

#### Scenario: Backing up
- WHEN the operator copies `/var/lib/boson`
- THEN the copy is enough to restore every project without recreating its GitHub App
