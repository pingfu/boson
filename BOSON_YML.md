# `_boson.yml`

The file a repository uses to tell boson what to deploy.

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
| `env` | no | Environment set to run with |
| `expire_after` | no | Idle time after which the deployment is stopped |

Entries are matched top to bottom and the first match wins, so `main` listed above `"*"` gets its own hostname rather than a generated one. Reordering the file is how matching changes.

`{branch}` in a hostname is the branch name as a DNS label: lowercased, anything outside `a-z0-9-` replaced with `-`, leading and trailing `-` trimmed, and a short hash added when the branch name is changed. `feature/add-search` becomes `feature-add-search-<hash>`.

## Hostnames

boson issues certificates and routes traffic for every declared name, the same way it does for a hostname given to `boson add`. Pointing DNS at the host stays the admin's job, including a wildcard record when a pattern entry generates names.

A hostname another active deployment already serves is a conflict: the deploy fails before it builds and `boson status` reports it.

`www` is an alias like any other. Add it under `aliases` when the deployment should redirect it.

## Environment sets

`env` names a set. The name is a filename, and the file is on the host, never in the repository:

```
env: production   →   /var/lib/boson/env/<org>/<name>/production
env: staging      →   /var/lib/boson/env/<org>/<name>/staging
env: (omitted)    →   no environment file; the deployment gets none
```

So this file:

```yaml
deployments:
  - branch: main
    hostname: example.org
    env: production
  - branch: "*"
    hostname: "{branch}.preview.example.org"
    env: preview
```

asks for two files on the server, whatever the branches turn out to be:

```
/var/lib/boson/env/acme/site/production    <- main
/var/lib/boson/env/acme/site/preview       <- every matched branch
```

Entries sharing a name share the file: three pattern entries naming `preview` read one file, and changing it changes what all three get on their next deploy. Entries naming different sets share nothing, which is the point.

The repository and the server keep separate halves of this. The repo commits a template (`.env.example`) so the shape is reviewable; the server holds the values. Nothing merges them: boson passes the set's path and compose reads that file alone, so a key present in the template and missing from the set is missing at runtime.

Checkouts live elsewhere, under `/srv`, one per branch:

```
/srv/acme/site/main/                 <- checkout: git working tree for main
/srv/acme/site/feature-add-search/   <- checkout, created when that branch was pushed
```

A checkout directory is named by the branch's slug, the same reduction that fills `{branch}` in a hostname, so a directory listing and a URL name the same thing. Keeping the sets out of `/srv` is what makes them safe: every path under it is git's to overwrite.

boson passes the chosen set's path to compose as `BOSON_ENV_FILE`, and the compose file consumes it:

```yaml
services:
  web:
    build: .
    image: my-app:${BOSON_COMMIT}
    ports:
      - "127.0.0.1:${BOSON_PORT}:8080"
    env_file: ${BOSON_ENV_FILE}
```

Four variables reach every `docker compose` boson runs: `BOSON_PORT`, the published port it allocated; `BOSON_ENV_FILE`, the path to the environment set this branch named; `BOSON_COMMIT`, the commit the deploy just fetched; and `BOSON_BRANCH_SLUG`, this branch's slug.

`BOSON_BRANCH_SLUG` is what keeps two branches out of each other's data. A compose file mounting a fixed host path gives every branch the same directory, so a staging deployment writes into production's database. Put the slug in the path instead:

```yaml
    volumes:
      - /var/lib/my-app/${BOSON_BRANCH_SLUG}:/data
```

A named volume needs the same treatment, since the name is shared exactly as a path is:

```yaml
volumes:
  data:
    name: my-app-${BOSON_BRANCH_SLUG}
```

Secrets stay out of the working tree, so a re-clone, a `git reset --hard`, a branch teardown or a purge cannot reach them, and a repository that commits its `.env` cannot overwrite them.

A named set with no file fails the deploy before the build, naming the path to create. `boson status` shows the deployment as configured but not deployable until it exists.

Pattern entries name a different set from production, or they hand every branch pushed to the repository the production credentials.

## Images

`images.keep` is how many builds of each image to retain, counting the one just built. Tagging with `${BOSON_COMMIT}` is what makes retention possible: each build gets a name of its own instead of overwriting `latest`, so the last few commits stay deployable without a rebuild and everything older is removed after a successful deploy.

Retention only touches images a deploy tagged with the commit it built. A service running an image someone else published is left alone.

## Lifecycle

A push deploys the branch it names, if an entry matches it.

`expire_after` measures from the last successful deploy, so an active branch never expires, and a branch whose recent deploys all failed is not kept alive by them. The daemon sweeps every fifteen minutes. On expiry the containers stop and the hostname and port are released, while the checkout stays: a later push redeploys the branch rather than starting from nothing.

An expired deployment leaves `boson status`, since it holds nothing any more. What stopped it is in `journalctl -u boson`.

## Project-level settings

`images` and anything else outside `deployments` is read from the repository's default branch, so two branches cannot disagree about one number.

## Validation

The file is read after fetch and before build, alongside the check on what compose publishes. A deploy fails, and `boson status` reports, when:

- the file does not parse, or `version` is unknown
- an entry omits `branch` or `hostname`
- two entries claim the same hostname, or the name belongs to another active deployment
- a `{branch}` hostname collides with one another deployment of the same repository generated
- `env` names a set that does not exist on the host
- no entry matches the branch a deploy was asked for

A push to a branch no entry matches deploys nothing, which is not an error: a repository decides which of its branches are deployed.

A repository with no `_boson.yml` has nothing to deploy, and says so before it builds.

`boson deploy <repo> --branch <name>` redeploys an existing deployment. Pattern deployments are created by pushes, because the push supplies the branch that matched the pattern.

## Undecided

**A ceiling on matched branches.** `expire_after` bounds how long a deployment lives, not how many exist. Fifteen branches pushed in a day is fifteen container stacks, each building an image, on one host. Either a project-wide cap that refuses past its limit, or an admission that the host's memory is the limit.

**Deployments removed from the file.** Today an entry that disappears stops that branch's next deploy with the reason, and leaves its containers running until `boson remove`. Tearing it down automatically is more consistent, and also means a bad merge takes production off the internet.

**A compose file per deployment.** A staging branch may want a different compose file from production. One field, `compose:`, and a default of `docker-compose.yml`.
