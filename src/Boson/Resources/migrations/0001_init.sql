CREATE TABLE schema_version (
  version    INTEGER PRIMARY KEY,
  applied_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE platform (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE projects (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  repo             TEXT NOT NULL,
  hostname         TEXT NOT NULL,
  upstream_port    INTEGER NOT NULL CHECK (upstream_port BETWEEN 1 AND 65535),
  branch           TEXT NOT NULL DEFAULT 'main',

  github_app_id          INTEGER NOT NULL,
  github_app_slug        TEXT    NOT NULL,
  github_installation_id INTEGER NOT NULL,
  github_webhook_secret  TEXT    NOT NULL,
  github_app_pem         TEXT    NOT NULL,

  webhook_active   INTEGER NOT NULL DEFAULT 0,
  deploy_pending   INTEGER NOT NULL DEFAULT 0,
  created_at       TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at       TEXT NOT NULL DEFAULT (datetime('now')),
  archived_at      TEXT
);

CREATE UNIQUE INDEX uniq_active_repo     ON projects(repo)          WHERE archived_at IS NULL;
CREATE UNIQUE INDEX uniq_active_hostname ON projects(hostname)      WHERE archived_at IS NULL;
CREATE UNIQUE INDEX uniq_active_port     ON projects(upstream_port) WHERE archived_at IS NULL;

CREATE TABLE deploys (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
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
