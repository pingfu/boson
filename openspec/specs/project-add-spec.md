# project-add Specification

## Purpose

`boson add <org/name> [--owner org|user]` registers a repository: it creates the project's GitHub App (see [github-app](github-app-spec.md)), clones the default branch and creates the deployments `_boson.yml` names.

## Requirements

### Requirement: Starting an add
The system SHALL refuse an add when the platform is not initialised or the repository is already an active project, and otherwise SHALL return a setup URL `https://<control-hostname>/_boson/setup-app/start?state=<token>`.

#### Scenario: Repository already added
- WHEN an active project exists for the repository
- THEN the command prints `repo already added: <org/name>` and exits 1

#### Scenario: Platform not initialised
- WHEN no control hostname is stored
- THEN the command prints `platform not initialised; run boson init first` and exits 1

#### Scenario: Add accepted
- WHEN the add is accepted
- THEN the command prints any warnings and the setup URL, and attempts to open the URL in a browser

### Requirement: Following the flow
The system SHALL poll the daemon once a second for the setup's phase and print a line each time it changes. The daemon owns the flow, and interrupting the command stops the display.

[bug] Interrupting `boson add` exits 1 with no output. `boson deploy` handles the same interruption with a message and exit 0.

| Phase | Meaning |
|---|---|
| `awaiting_manifest` | Waiting for the App to be created on GitHub |
| `awaiting_install` | App created, waiting for installation |
| `finalizing` | Saving the project |
| `fetching` | Declaring deployments |
| `done` | Project saved and declared |
| `fetch_failed` | Project saved, declaration stopped with a reason |
| `declare_failed` | Project saved, declaration threw |
| `failed` | Nothing saved |

#### Scenario: Setup expires or the daemon restarts
- WHEN the daemon no longer knows the setup token
- THEN the command reports that nothing was persisted and that an orphaned App is deleted by hand, and exits 2

[bug] The 15 minutes run from `boson add`, and expiry ignores the phase. A setup still cloning at minute 15 is dropped while its finalisation carries on, so the command says nothing was persisted when the project is saved. A daemon restart after the save does the same. `boson add` then answers `repo already added`.

### Requirement: Declaration
The system SHALL declare a saved project by minting an installation token, reading the repository's default branch from `GET /repos/<org>/<name>`, fetching that branch into `/srv/<org>/<name>/<slug>`, reading `_boson.yml` from the checkout, and creating one deployment for each entry that names a branch outright. Deployments of pattern entries are created by pushes.

Declarations run one at a time. A project that already has an active deployment is left as it is.

#### Scenario: Project removed while it is being declared
- WHEN `boson remove` archives or purges the project while its declaration is fetching or creating deployments
- THEN the declaration stops with `<org/name> was removed while it was being declared`, and any deployment it had created is archived, releasing its hostname and port

#### Scenario: File names two branches and one pattern
- WHEN `_boson.yml` has entries for `main`, `develop` and `"*"`
- THEN deployments are created for `main` and `develop`, each with its own allocated port
- AND Caddy is synchronised and the App's webhook address is moved

#### Scenario: Project already declared
- WHEN declaration runs for a project with an active deployment
- THEN it returns without fetching or creating anything

#### Scenario: Creating a deployment fails partway
- WHEN an error occurs after some deployments were created
- THEN those deployments are archived, so the next attempt creates the whole set

### Requirement: Declaration errors
The system SHALL stop a declaration, keeping the project and its App, when the repository cannot be read or the file cannot be used, and SHALL report the reason.

#### Scenario: Installation cannot read the repository
- WHEN GitHub answers 404 for the repository
- THEN the error says the App is not installed on the owner or was not granted the repository, and gives both ways on: grant it and run `boson deploy`, or `boson remove --purge`, delete the App and add again

#### Scenario: Fetch fails
- WHEN the token mint fails with any status, the repository lookup fails with anything but 404, or the git init, fetch or reset fails
- THEN the error is `initial fetch failed: <reason>`

#### Scenario: No file
- WHEN the checkout has no `_boson.yml`
- THEN the error is `no _boson.yml at the root of <org/name>`

#### Scenario: File does not validate
- WHEN `_boson.yml` fails validation
- THEN the error is the validation message from [boson-file](boson-file-spec.md)

### Requirement: Declaration warnings
The system SHALL attach warnings to a successful declaration for conditions the operator has to act on. The DNS checks cover the hostname of each entry that names a branch outright. Aliases are not resolved.

#### Scenario: Hostname does not resolve
- WHEN such a hostname resolves to no address
- THEN a warning says to create the record, or Caddy will keep retrying certificate issuance

#### Scenario: Hostname resolves elsewhere
- WHEN such a hostname resolves to an address on no local interface
- THEN a warning says so and that this is normal behind NAT

#### Scenario: Default branch not declared
- WHEN no entry matches the default branch
- THEN a warning says `_boson.yml declares no deployment for <branch>, the default branch`

### Requirement: Outcome
The system SHALL end the command according to the final phase.

#### Scenario: Done
- WHEN the phase is `done`
- THEN the command prints `✓ <org/name> added`, the warnings, and the next steps, and exits 0
- AND the first step lists the path of every environment set `_boson.yml` names, or says none is named
- AND the second step is `boson deploy <org/name>`

[bug] When the file names more than one branch, that command is refused with `name one with --branch` and exits 3.

#### Scenario: Fetch failed
- WHEN the phase is `fetch_failed`
- THEN the command prints `✓ <org/name> added` with the error as a warning, tells the operator to fix it and push, and exits 0

#### Scenario: Declare failed
- WHEN the phase is `declare_failed`
- THEN the command prints the error, states that the project and its App are saved with no deployments, gives `boson deploy <org/name>` to continue and `boson remove <org/name> --purge` to start over, and exits 2

#### Scenario: Failed
- WHEN the phase is `failed`
- THEN the command prints `add failed: <reason>` and `nothing was persisted; re-run boson add to start over`, and exits 2
