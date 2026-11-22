-- The organisation a project's GitHub App was created under, which is where
-- its settings page lives. Null is a personal account's App, and that is what
-- every row older than this column holds.
ALTER TABLE projects ADD COLUMN github_app_org TEXT;
