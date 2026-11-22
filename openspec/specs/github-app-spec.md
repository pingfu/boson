# github-app Specification

## Purpose

Each project has its own private GitHub App, created through GitHub's App manifest flow in the operator's browser. The App gives boson read access to the repository and delivers push webhooks.

## Requirements

### Requirement: Owner kind
The system SHALL determine whether the repository's owner is an organisation or a personal account before starting the flow: from `--owner org|user` when given, otherwise from `GET https://api.github.com/users/<owner>` without credentials.

#### Scenario: Owner is an organisation
- **WHEN** GitHub reports the owner's `type` as `Organization`
- **THEN** the App is created at `https://github.com/organizations/<owner>/settings/apps/new`

#### Scenario: Owner is a personal account
- **WHEN** the owner is a personal account
- **THEN** the App is created at `https://github.com/settings/apps/new`
- **AND** the add response carries a warning that the App is created under whichever account is signed in to GitHub in the browser

#### Scenario: Owner does not exist
- **WHEN** GitHub answers 404 for the owner
- **THEN** the add is refused with `GitHub has no account named <owner>`

#### Scenario: GitHub cannot be asked
- **WHEN** the lookup fails for any other reason, including rate limiting
- **THEN** the add is refused with a message telling the operator to re-run with `--owner org` or `--owner user`

### Requirement: Pending setup
The system SHALL hold each setup in daemon memory, keyed by a state token of 16 random bytes in lowercase hex, and SHALL discard it 15 minutes after creation.

#### Scenario: Setup abandoned
- **WHEN** the operator never completes the browser flow
- **THEN** the entry expires after 15 minutes and nothing is persisted

#### Scenario: Daemon restarts mid-setup
- **WHEN** the daemon restarts before the flow completes
- **THEN** the setup is gone and every setup URL for it answers 404

### Requirement: Start page
The system SHALL serve `GET /_boson/setup-app/start?state=<token>` as an HTML page that posts the App manifest to GitHub, and SHALL answer 404 for an unknown or expired token.

The manifest contains:

| Field | Value |
|---|---|
| `name` | `boson-<name>` |
| `url` | `https://github.com/<org>/<name>` |
| `redirect_url` | `https://<control-hostname>/_boson/setup-app/callback` |
| `setup_url` | `https://<control-hostname>/_boson/setup-app/installed` |
| `hook_attributes.url` | `https://<control-hostname>/_boson/webhook/<org>/<name>` |
| `public` | `false` |
| `default_events` | `["push"]` |
| `default_permissions` | `contents: read`, `metadata: read` |

#### Scenario: Live token
- **WHEN** the operator opens the setup URL within 15 minutes
- **THEN** the page is served with the manifest and the token embedded

### Requirement: Manifest callback
The system SHALL handle `GET /_boson/setup-app/callback?code=<code>&state=<token>` by exchanging the code at `POST /app-manifests/<code>/conversions` for the App's id, slug, private key and webhook secret, and redirecting to `https://github.com/apps/<slug>/installations/new?state=<token>`.

#### Scenario: Missing code or state
- **WHEN** either query parameter is absent
- **THEN** the response is 404

#### Scenario: Token unknown or already used
- **WHEN** the token has no live setup, or its setup is past the manifest step
- **THEN** the response is 404

#### Scenario: Conversion fails
- **WHEN** GitHub rejects the conversion
- **THEN** the setup's phase becomes `failed` with GitHub's message, and the response is 404

### Requirement: Installation callback
The system SHALL handle `GET /_boson/setup-app/installed?state=<token>&installation_id=<id>` by recording the installation id, starting finalisation in the background and serving a success page.

#### Scenario: First visit
- **WHEN** GitHub redirects the browser here after the install
- **THEN** finalisation starts and the success page is served without waiting for it

#### Scenario: Repeat visit
- **WHEN** the URL is opened again for a setup that has not failed
- **THEN** the success page is served and finalisation is not started a second time

#### Scenario: Failed setup or missing installation id
- **WHEN** the setup's phase is `failed`, the token is unknown, or `installation_id` is not a number
- **THEN** the response is 404

### Requirement: Credential persistence
The system SHALL insert the project row whole, with the App id, slug, installation id, webhook secret, private key and owning organisation, once all of them are known. The system SHALL keep a saved project through any later failure of the flow.

#### Scenario: Flow fails before the install
- **WHEN** the flow fails or expires before the installation callback
- **THEN** no project row exists

#### Scenario: Fetch fails after the save
- **WHEN** the project is saved and the first fetch then fails
- **THEN** the project row and its credentials remain, and `boson deploy` completes the project

### Requirement: Installation tokens
The system SHALL authenticate to GitHub as the App with an RS256 JWT signed by the project's private key, with `iss` the App id, `iat` 60 seconds in the past and `exp` 540 seconds ahead, and SHALL exchange it at `POST /app/installations/<installation-id>/access_tokens` for an installation token. A fresh token is minted for every fetch. The token is never written to disk.

#### Scenario: Two deploys of one project
- **WHEN** a project deploys twice
- **THEN** each deploy mints its own installation token

### Requirement: Webhook address
The system SHALL move the App's webhook address off the control hostname once the project has deployments, by `PATCH /app/hook/config` with `https://<hostname>/_boson/webhook/<org>/<name>`, where the hostname belongs to the default branch's deployment, or to the first active deployment when the default branch has none.

#### Scenario: Default branch is deployed
- **WHEN** declaration creates a deployment for the default branch
- **THEN** the App's webhook address is set to that deployment's hostname

#### Scenario: GitHub refuses the change
- **WHEN** the `PATCH` fails
- **THEN** the declaration succeeds with a warning that gives the address to set by hand

#### Scenario: Every entry is a pattern
- **WHEN** `_boson.yml` names no branch outright
- **THEN** the address stays on the control hostname and a warning says so

### Requirement: App settings URL
The system SHALL report where a project's App is managed: `https://github.com/organizations/<org>/settings/apps/<slug>` for an App owned by an organisation, and `https://github.com/settings/apps/<slug>` otherwise. boson leaves deleting the App to the operator.

#### Scenario: Organisation-owned App
- **WHEN** a project whose App was created under an organisation is removed
- **THEN** the URL printed is the organisation's settings page for that App
