# environment-sets Specification

## Purpose

An environment set is a file of environment values kept on the host, outside the checkout. A `_boson.yml` entry names one with `env`, and boson passes its path to Docker Compose together with the other values a compose file needs from boson.

## Requirements

### Requirement: Location
The system SHALL resolve an environment set named `<set>` for repository `<org>/<name>` to `/var/lib/boson/env/<org>/<name>/<set>`. Entries naming the same set share the file.

#### Scenario: Two entries, one set
- **WHEN** the entries for `"*"` and `feature/*` both declare `env: preview`
- **THEN** every deployment they produce reads `/var/lib/boson/env/<org>/<name>/preview`

### Requirement: Named set must exist
The system SHALL fail a deploy before running compose when the deployment names an environment set whose file does not exist, with an error naming the set, the branch and the path.

#### Scenario: Set file missing
- **WHEN** the entry declares `env: production` and `/var/lib/boson/env/acme/site/production` does not exist
- **THEN** the deploy fails with `_boson.yml names env set production for main, and /var/lib/boson/env/acme/site/production does not exist`
- **AND** nothing is built

### Requirement: Entry without a set
When an entry declares no `env`, the system SHALL pass the path of the set named `default` without requiring the file to exist.

#### Scenario: No env declared
- **WHEN** the entry has no `env` and no `default` file exists
- **THEN** the deploy proceeds, with `BOSON_ENV_FILE` set to `/var/lib/boson/env/<org>/<name>/default`

### Requirement: Compose variables
The system SHALL supply these variables in the process environment of the `docker compose config` and `docker compose up` it runs for a deployment:

| Variable | Value |
|---|---|
| `BOSON_PORT` | The loopback port allocated to the deployment |
| `BOSON_ENV_FILE` | The path of the deployment's environment set |
| `BOSON_COMMIT` | The full SHA of the commit the deploy fetched |
| `BOSON_BRANCH_SLUG` | The deployment's branch slug |

The values are never written into the checkout.

#### Scenario: Compose file consumes the variables
- **WHEN** a compose file declares `ports: ["127.0.0.1:${BOSON_PORT}:8080"]`, `env_file: ${BOSON_ENV_FILE}` and `image: my-app:${BOSON_COMMIT}`
- **THEN** compose resolves them to the deployment's port, set path and commit

#### Scenario: Checkout is reset
- **WHEN** a deploy runs `git reset --hard` in the checkout
- **THEN** the environment set is unaffected, because it is outside `/srv`

### Requirement: Sets reported at add
The system SHALL report, when `boson add` completes, the path of every distinct environment set named in `_boson.yml`, sorted by name.

#### Scenario: File names two sets
- **WHEN** the entries declare `production` and `preview`
- **THEN** `boson add` lists `/var/lib/boson/env/<org>/<name>/preview` and `/var/lib/boson/env/<org>/<name>/production` as files to create
