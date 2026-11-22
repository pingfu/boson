# boson

boson is a single-host Docker PaaS. A CLI and a resident daemon, shipped as one .NET binary, deploy branches of GitHub repositories as Docker Compose projects behind Caddy. Each repository reaches GitHub through its own GitHub App, and declares what it deploys in `_boson.yml`.

## Specs

One file per capability, at `openspec/specs/<capability>-spec.md`.

| Spec | Covers |
|---|---|
| [platform-install](specs/platform-install-spec.md) | `boson init` |
| [platform-uninstall](specs/platform-uninstall-spec.md) | `boson uninstall` |
| [github-app](specs/github-app-spec.md) | App creation by manifest, credentials, installation tokens, webhook address |
| [project-add](specs/project-add-spec.md) | `boson add` and declaring a project's deployments |
| [project-remove](specs/project-remove-spec.md) | `boson remove` |
| [boson-file](specs/boson-file-spec.md) | `_boson.yml` schema, matching, validation, branch slugs |
| [environment-sets](specs/environment-sets-spec.md) | Environment sets and the variables passed to compose |
| [deploy](specs/deploy-spec.md) | `boson deploy` and the deploy pipeline |
| [webhook-deploy](specs/webhook-deploy-spec.md) | Push-to-deploy |
| [routing](specs/routing-spec.md) | Caddy configuration, hostnames, TLS |
| [deployment-lifecycle](specs/deployment-lifecycle-spec.md) | Expiry, teardown, image retention |
| [status](specs/status-spec.md) | `boson status` |
| [daemon](specs/daemon-spec.md) | `boson serve`, listeners, RPC, shutdown |
| [storage](specs/storage-spec.md) | SQLite schema, migrations, file ownership |

## Spec format

```markdown
# <capability> Specification

## Purpose

## Requirements

### Requirement: <name>
The system SHALL ...

#### Scenario: <name>
- WHEN ...
- THEN ...
```

A requirement states behaviour with SHALL. Each requirement has at least one scenario.

## Glossary

| Term | Meaning |
|---|---|
| Project | A GitHub repository, `org/name`, and the GitHub App that reaches it |
| Deployment | One branch of a project: a hostname, a loopback port, a checkout and a compose project |
| Deploy | One run of the pipeline for a deployment, with a row in `deploys` and a log file |
| Environment set | A file of environment values on the host, named by `env` in `_boson.yml` |
| Branch slug | A branch name reduced to a DNS label. Names the checkout directory, fills `{branch}` in a hostname and is part of the compose project name |
| Control hostname | The hostname given to `boson init`. Serves `/_boson/*` |
| Platform stack | The compose project `boson`, which runs Caddy |
| Declaration | Creating a project's deployments from the `_boson.yml` on its default branch |

## Host layout

| Path | Holds |
|---|---|
| `/usr/local/bin/boson` | The binary |
| `/etc/systemd/system/boson.service` | The daemon's unit |
| `/var/lib/boson/boson.db` | The database |
| `/var/lib/boson/boson.sock` | The CLI RPC socket |
| `/var/lib/boson/locks/_platform.lock` | The init and uninstall lock |
| `/var/lib/boson/env/<org>/<name>/<set>` | Environment sets |
| `/var/log/boson/boson.log` | The daemon log |
| `/var/log/boson/deploys/<id>.log` | One log per deploy |
| `/srv/<org>/<name>/<branch-slug>/` | One checkout per deployment |

`BOSON_DATA_DIR`, `BOSON_LOG_DIR` and `BOSON_SRV_DIR` override `/var/lib/boson`, `/var/log/boson` and `/srv` for foreground development.

## Repository names

A repository argument is trimmed, stripped of leading and trailing `/`, and lowercased. It is valid when it matches `^[a-z0-9]([a-z0-9\-_.]*[a-z0-9])?/[a-z0-9\-_.]+$`. Every command that takes one exits 1 with `invalid repo (expected org/name): <input>` when it does not.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | User error: bad argument, unknown project, refused request |
| 2 | Runtime failure: daemon unreachable, schema mismatch, failed deploy, failed host command |
| 3 | Another init or uninstall holds the platform lock, or the daemon refused a deploy with 409: a deploy of that branch is running, or the project has several deployments and no `--branch` was given |
| 99 | Internal error. The exception is printed with a request to file an issue |

[bug] `Program.cs` maps a schema mismatch to 2 and an unexpected exception to 99, and neither mapping runs. System.CommandLine's default exception handler catches every exception a command lets escape, prints `Unhandled exception:` with a stack trace and exits 1. A cancelled or timed-out operation exits 1 and prints nothing. Reproduced with `boson deploy acme/site` against an empty data directory: stack trace, exit 1. `boson uninstall` catches the schema mismatch itself and does exit 2.
