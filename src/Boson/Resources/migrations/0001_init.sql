CREATE TABLE schema_version (
  version    INTEGER PRIMARY KEY,
  applied_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE platform (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
-- Known keys: admin_hostname, installed_at, binary_version.

CREATE TABLE projects (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  repo             TEXT NOT NULL,   -- "org/name", stored lowercase: GitHub compares
                                    -- names case-insensitively, and the row, the
                                    -- filesystem path and the webhook URL must agree
  hostname         TEXT NOT NULL,
  upstream_port    INTEGER NOT NULL CHECK (upstream_port BETWEEN 1 AND 65535),
  branch           TEXT NOT NULL DEFAULT 'main',

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

  webhook_active   INTEGER NOT NULL DEFAULT 0,  -- 1 once a deploy has succeeded
  deploy_pending   INTEGER NOT NULL DEFAULT 0,  -- a push arrived mid-deploy; the
                                                -- running deploy drains it. Set on
                                                -- an idle project it means a
                                                -- redeploy was dropped

  created_at       TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at       TEXT NOT NULL DEFAULT (datetime('now')),
  archived_at      TEXT
);

-- Active-scoped, not global: `boson remove` archives rather than deletes, and
-- these partial indexes are what let it release the repo, hostname and port so
-- the same project can be added again.
CREATE UNIQUE INDEX uniq_active_repo     ON projects(repo)          WHERE archived_at IS NULL;
CREATE UNIQUE INDEX uniq_active_hostname ON projects(hostname)      WHERE archived_at IS NULL;
CREATE UNIQUE INDEX uniq_active_port     ON projects(upstream_port) WHERE archived_at IS NULL;

CREATE TABLE deploys (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  -- References the surrogate id, not repo: history then follows a project's
  -- archived row instead of being orphaned by a remove, and --purge cascades
  -- it away.
  project_id  INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  "trigger"   TEXT NOT NULL,
  commit_sha  TEXT,
  started_at  TEXT NOT NULL DEFAULT (datetime('now')),
  finished_at TEXT,
  status      TEXT NOT NULL DEFAULT 'running',
  log_path    TEXT NOT NULL,
  error       TEXT
);

CREATE INDEX idx_deploys_project_started ON deploys(project_id, started_at DESC);
