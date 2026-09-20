CREATE TABLE schema_version (
  version    INTEGER PRIMARY KEY,
  applied_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE platform (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
-- Known keys: admin_hostname, installed_at, binary_version.

-- A repository and the GitHub App that reaches it. What it deploys is one row
-- per branch in deployments, because `_boson.yml` can name several.
CREATE TABLE projects (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  repo             TEXT NOT NULL,   -- "org/name", stored lowercase: GitHub compares
                                    -- names case-insensitively, and the row, the
                                    -- filesystem path and the webhook URL must agree

  -- GitHub issues these once during the manifest flow and will not re-issue
  -- them. Every column is NOT NULL so a row cannot exist half-credentialed:
  -- the row is inserted whole or not at all, and its existence means the
  -- project is fully added. Losing them means re-running `boson add` and
  -- deleting the orphaned App by hand. Stored in plaintext; the DB is 0600.
  github_app_id          INTEGER NOT NULL,
  github_app_slug        TEXT    NOT NULL,
  github_installation_id INTEGER NOT NULL,
  github_webhook_secret  TEXT    NOT NULL,
  github_app_pem         TEXT    NOT NULL,

  created_at       TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at       TEXT NOT NULL DEFAULT (datetime('now')),
  archived_at      TEXT
);

-- Active-scoped, not global: `boson remove` archives rather than deletes, and
-- this partial index is what lets it release the repo so the same project can
-- be added again.
CREATE UNIQUE INDEX uniq_active_repo ON projects(repo) WHERE archived_at IS NULL;

-- One branch of one repository: what has a hostname, a port, a checkout and a
-- set of containers.
CREATE TABLE deployments (
  id             INTEGER PRIMARY KEY AUTOINCREMENT,
  project_id     INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,

  branch         TEXT NOT NULL,   -- the true ref name, which the label cannot
                                  -- be reversed into
  dns_label      TEXT NOT NULL,   -- branch reduced to a DNS label: the checkout
                                  -- directory and the compose project are named
                                  -- after it, so both match the hostname on sight
  hostname       TEXT NOT NULL COLLATE NOCASE,
  host_port      INTEGER NOT NULL CHECK (host_port BETWEEN 1 AND 65535),

  env_set        TEXT,            -- null when the branch names none
  expire_after   TEXT,            -- e.g. "14d"; null keeps the deployment

  webhook_active INTEGER NOT NULL DEFAULT 0,  -- 1 once a deploy has succeeded
  deploy_pending INTEGER NOT NULL DEFAULT 0,  -- a push arrived mid-deploy; the
                                              -- running deploy drains it. Set on
                                              -- an idle deployment it means a
                                              -- redeploy was dropped

  created_at     TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at     TEXT NOT NULL DEFAULT (datetime('now')),
  archived_at    TEXT
);

-- Active-scoped for the same reason as the repo index: tearing a branch down
-- archives it, and the name, port and directory have to come free.
CREATE UNIQUE INDEX uniq_active_branch   ON deployments(project_id, branch) WHERE archived_at IS NULL;
CREATE UNIQUE INDEX uniq_active_label    ON deployments(project_id, dns_label) WHERE archived_at IS NULL;
CREATE UNIQUE INDEX uniq_deployment_host ON deployments(hostname)           WHERE archived_at IS NULL;
CREATE UNIQUE INDEX uniq_deployment_port ON deployments(host_port)          WHERE archived_at IS NULL;

-- Every public name held by an active deployment. The canonical hostname is
-- stored here too, so an alias cannot collide with another deployment's
-- hostname or another alias.
CREATE TABLE deployment_names (
  deployment_id INTEGER NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
  -- NOCASE because DNS is: Example.org and example.org are one name, and
  -- storing both would put two Caddy routes on it with the first winning.
  name          TEXT NOT NULL COLLATE NOCASE,
  kind          TEXT NOT NULL CHECK (kind IN ('primary', 'alias'))
);

CREATE UNIQUE INDEX uniq_deployment_name ON deployment_names(name);
CREATE INDEX idx_deployment_names_deployment ON deployment_names(deployment_id);

CREATE TABLE deploys (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  -- References the surrogate id, not the branch: history then follows a
  -- deployment's archived row instead of being orphaned by a teardown, and
  -- --purge cascades it away.
  deployment_id INTEGER NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
  "trigger"     TEXT NOT NULL,
  commit_sha    TEXT,
  started_at    TEXT NOT NULL DEFAULT (datetime('now')),
  finished_at   TEXT,
  status        TEXT NOT NULL DEFAULT 'running',
  log_path      TEXT NOT NULL,
  error         TEXT
);

CREATE INDEX idx_deploys_deployment_started ON deploys(deployment_id, started_at DESC);
