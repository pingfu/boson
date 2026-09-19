# `_boson.yml`

Design for the file a repository uses to tell boson what to deploy. Not built yet.

`_boson.yml` sits at the repository root and is read on every deploy, from the branch being deployed. It declares branches, the hostnames they serve, and which environment set they run with. It never contains secrets.

```yaml
version: 1

deployments:
  - branch: main
    hostname: example.org
    aliases: [www.example.org]
    env: production

  - branch: develop
    hostname: staging.example.org
    env: staging
    expire_after: 30d

  - branch: "*"
    hostname: "{branch}.example.org"
    env: preview
    expire_after: 14d

images:
  keep: 3
```

## Deployments

Each entry answers one question: this branch, on this hostname. An entry naming a single branch produces one deployment. An entry whose `branch` is a pattern produces one per matching branch, created when that branch is first pushed.

| Field | Required | Meaning |
|---|---|---|
| `branch` | yes | A branch name, or a glob (`*`, `feature/*`) |
| `hostname` | yes | The name this deployment serves. Supports `{branch}` |
| `aliases` | no | Names that 308 to `hostname` |
| `env` | no | Environment set to run with, default `default` |
| `expire_after` | no | Idle time after which the deployment is stopped |

Entries are matched top to bottom and the first match wins, so `main` listed above `"*"` gets its own hostname rather than a generated one. Reordering the file is how matching changes.

`{branch}` in a hostname is the branch name as a DNS label: lowercased, anything outside `a-z0-9-` replaced with `-`, leading and trailing `-` trimmed, truncated to 63 characters. `feature/add-search` becomes `feature-add-search`.

## Hostnames

boson issues certificates and routes traffic for every declared name, the same way it does for a hostname given to `boson add`. Pointing DNS at the host stays the admin's job, including a wildcard record when a pattern entry generates names.

A hostname another active deployment already serves is a conflict: the deploy fails before it builds and `boson status` reports it.

Listing an alias replaces the automatic `www` redirect, which applies only when an entry declares no aliases.

## Environment sets

`env` names a set. Sets live on the host, beside the checkouts rather than in one. Each branch gets its own working tree, and the project directory above them holds what belongs to the project:

```
/srv/marketcanary6/market-canary/
  env/
    production                 <- an environment set, created by the admin
    preview
  main/                        <- checkout: git working tree for the main branch
  develop/                     <- checkout
  feature-add-search/          <- checkout, created when that branch was pushed
```

A project added before this change keeps its checkout at `/srv/<org>/<name>` and its `.env` inside it, so migrating means moving the tree down into `<branch>/` and the `.env` out into `env/`.

boson passes the chosen set's path to compose as `BOSON_ENV_FILE`, and the compose file consumes it:

```yaml
services:
  web:
    build: .
    ports:
      - "127.0.0.1:${BOSON_HOST_PORT}:8080"
    env_file: ${BOSON_ENV_FILE}
```

Secrets stay out of the working tree, so a re-clone, a `git reset --hard`, a branch teardown or a purge cannot reach them, and a repository that commits its `.env` cannot overwrite them.

A named set with no file fails the deploy before the build, naming the path to create. `boson status` shows the deployment as configured but not deployable until it exists.

Pattern entries name a different set from production, or they hand every branch pushed to the repository the production credentials.

## Images

`images.keep` is how many tagged images to retain per deployment. Each build is tagged with the commit it came from, so retention keeps the last few commits deployable without a rebuild, and anything older is removed after a successful deploy.

## Lifecycle

A push deploys the branch it names, if an entry matches it.

Deleting a branch tears down what it was serving: containers stopped, route removed, host port released, checkout deleted.

`expire_after` measures from the last successful deploy, so an active branch never expires. On expiry the deployment is stopped and its port released, and the record stays visible in `boson status` with the reason. A later push redeploys it.

## Project-level settings

`images` and anything else outside `deployments` is read from the repository's default branch, so two branches cannot disagree about one number.

## Validation

The file is read after fetch and before build, alongside the check on what compose publishes. A deploy fails, and `boson status` reports, when:

- the file does not parse, or `version` is unknown
- an entry omits `branch` or `hostname`
- two entries claim the same hostname, or the name belongs to another active deployment
- a `{branch}` hostname collides with one another deployment of the same repository generated
- `env` names a set that does not exist on the host
- no entry matches the branch that was pushed, which deploys nothing and is not an error

A repository with no `_boson.yml` keeps the hostname, branch and environment file recorded when it was added.

## Undecided

**A ceiling on matched branches.** `expire_after` bounds how long a deployment lives, not how many exist. Fifteen branches pushed in a day is fifteen container stacks, each building an image, on one host. Either a project-wide cap that refuses past its limit, or an admission that the host's memory is the limit.

**Deployments removed from the file.** Deleting an entry could tear its deployment down on the next deploy of any branch, which is consistent and also means a bad merge takes production off the internet. Leaving it running until `boson remove` is safer and leaves the file disagreeing with reality.

**A compose file per deployment.** A staging branch may want a different compose file from production. One field, `compose:`, and a default of `docker-compose.yml`.
