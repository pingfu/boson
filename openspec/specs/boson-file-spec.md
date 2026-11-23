# boson-file Specification

## Purpose

`_boson.yml` at the root of a repository declares which branches deploy, the hostnames they serve and the environment set each runs with. It is read from the checkout of the branch being deployed, on every deploy.

## Requirements

### Requirement: Schema
The system SHALL read `_boson.yml` as YAML with these keys, ignoring any other key:

| Key | Type | Required | Default |
|---|---|---|---|
| `version` | integer | yes, must be `1` | |
| `deployments` | list of entries | yes, at least one | |
| `images.keep` | integer, at least 1 | no | `3` |

[bug] `BOSON_YML.md` says `images` is read from the default branch. `Deployer` uses the value in the file of the branch being deployed, so two branches can disagree.

Each entry:

| Key | Type | Required | Meaning |
|---|---|---|---|
| `branch` | string | yes | A branch name, or a pattern containing `*` |
| `hostname` | string | yes | The name the deployment serves |
| `aliases` | list of strings | no | Names that redirect to `hostname` |
| `env` | string | no | Environment set to run with |
| `expire_after` | string | no | Idle time before the deployment is stopped |

#### Scenario: Minimal file
- WHEN the file holds `version: 1` and one entry with `branch: main` and `hostname: example.org`
- THEN it is valid, and `images.keep` is 3

#### Scenario: Unknown key
- WHEN the file holds a key the schema does not list
- THEN the key is ignored and the file is valid

### Requirement: Normalisation
The system SHALL trim `branch`, and trim and lowercase `hostname` and every alias, before using them.

#### Scenario: Mixed-case hostname
- WHEN an entry declares `hostname: Example.ORG`
- THEN the deployment serves `example.org`

### Requirement: Branch matching
The system SHALL match a branch against entries from top to bottom and use the first that matches. An entry without `*` matches by exact, case-sensitive comparison. In an entry with `*`, a `*` matches a run of characters, including `/`, and the branch must be at least as long as the pattern's literal text plus one character per `*`. With one `*`, that means it stands for one or more characters. With several, one of them can match nothing.

#### Scenario: Named entry above a pattern
- WHEN the file lists `main` and then `"*"`, and `main` is deployed
- THEN the `main` entry is used

#### Scenario: Pattern spans slashes
- WHEN the entry is `feature/*` and the branch is `feature/a/b`
- THEN the entry matches

#### Scenario: Wildcard needs a character
- WHEN the entry is `feature/*` and the branch is `feature/`
- THEN the entry does not match

#### Scenario: Several wildcards
- WHEN the entry is `a*b*c`
- THEN `abxxc` matches, with the first `*` empty, and `abxc` does not, being too short

#### Scenario: No entry matches
- WHEN no entry matches the branch
- THEN the branch has no deployment declared

### Requirement: Branch placeholder
The system SHALL replace `{branch}` in a hostname with the branch's slug. A pattern entry's hostname SHALL contain `{branch}`, and an entry naming one branch SHALL NOT.

#### Scenario: Pattern hostname
- WHEN the entry is `branch: "*"`, `hostname: "{branch}.example.org"` and the branch is `develop`
- THEN the hostname is `develop.example.org`

[bug] A pattern entry with an alias gives every matching branch the same alias, so the second branch's deploy fails with the alias reported as already served by the first. `{branch}` in an alias is rejected by validation.

### Requirement: Branch slug
The system SHALL reduce a branch name to a slug of `a-z`, `0-9` and `-`, at most 63 characters, as follows:

1. Lowercase the name.
2. Keep `a-z` and `0-9`. Replace each run of other characters with one `-`.
3. Trim leading and trailing `-`.
4. When the result is empty, the slug is the first 6 hex characters of the SHA-256 of the branch name.
5. When the result differs from the branch name, or is longer than 63 characters, cut it to 56 characters, trim trailing `-`, and append `-` and the 6 hex characters.

#### Scenario: Name already a slug
- WHEN the branch is `main`
- THEN the slug is `main`

#### Scenario: Name with a slash
- WHEN the branch is `feature/add-search`
- THEN the slug is `feature-add-search-<6 hex>`

#### Scenario: Two branches reducing to the same text
- WHEN the branches are `Feature/X` and `feature-x`
- THEN their slugs differ, because the first carries a hash suffix and the second is unchanged

### Requirement: Validation
The system SHALL reject a file with the first problem found and report it with the message shown. The order is: parse, version, no entries, then for each entry from the top its branch, hostname, empty-alias, placeholder, name-syntax, duplicate-name, `env` and `expire_after` checks, then `images.keep`.

| Problem | Message |
|---|---|
| YAML does not parse, or a value has the wrong type | `_boson.yml does not parse: <YamlDotNet message>`. A wrong type reads `Exception during deserialization`. The message carries no line or column |
| `version` is not 1 | `_boson.yml declares version <n>; this boson reads version 1` |
| No entries | `_boson.yml declares no deployments` |
| Entry without `branch` | `_boson.yml has a deployment with no branch` |
| Entry without `hostname` | `_boson.yml: deployment for <branch> has no hostname` |
| Pattern entry without `{branch}` | `_boson.yml: <branch> matches many branches, so its hostname needs {branch} in it` |
| `hostname` or an alias is not a DNS name | `_boson.yml: "<name>" is not a hostname boson can serve for <branch>; write a DNS name made of letters, digits, '-' and '.', such as example.org` |
| Single-branch entry with `{branch}` | `_boson.yml: <branch> names one branch, so {branch} in its hostname has nothing to vary` |
| Hostname or alias used twice, compared case-insensitively | `_boson.yml: <name> is claimed by both <branch> and <branch>` |
| `env` is not a file name of letters, digits, `.`, `_` and `-`, or starts with `.` | `_boson.yml: env for <branch> is "<value>"; an environment set is a file name made of letters, digits, '.', '_' and '-', and does not start with '.'` |
| `expire_after` is not a positive whole number followed by `d`, `h` or `m`, or is too large to be a time span | `_boson.yml: expire_after for <branch> is "<value>"; write a whole number followed by d, h or m, such as 14d` |
| `images.keep` below 1 | `_boson.yml: images.keep is <n>, and a deployment needs at least one image` |

A DNS name is dot-separated labels of `a-z`, `0-9` and `-`, each 1 to 63 characters and neither starting nor ending with `-`, 253 characters in all. A pattern entry's hostname is checked with `{branch}` filled in. Aliases take no `{branch}`.

#### Scenario: Wildcard or placeholder hostname
- WHEN an entry declares `hostname: "*.example.org"` or `hostname: "{http.request.host}"`
- THEN the file is rejected, because Caddy would match hostnames the file never named

#### Scenario: Two names in one alias
- WHEN an entry declares `aliases: ["a.example.org,b.example.org"]`
- THEN the file is rejected

#### Scenario: Two entries claim one hostname
- WHEN entries for `main` and `develop` both declare `example.org`, as a hostname or an alias
- THEN the file is rejected with `_boson.yml: example.org is claimed by both main and develop`

#### Scenario: Invalid file on deploy
- WHEN a deploy reads a file that fails validation
- THEN the deploy fails before it builds, with the validation message as its error

#### Scenario: Key with no value
- WHEN the file holds `aliases:`, `env:`, `expire_after:` or `images:` with nothing after it
- THEN the key is treated as absent

#### Scenario: Empty entries
- WHEN the file holds `deployments:` with nothing after it, a bare `-` entry, or an empty alias
- THEN it is rejected with `_boson.yml declares no deployments`, `_boson.yml has an empty entry under deployments`, or `_boson.yml: deployment for <branch> has an empty alias`

### Requirement: The file is the authority
The system SHALL apply the matching entry's `hostname`, `aliases`, `env` and `expire_after` to the stored deployment on every deploy, and synchronise Caddy when any of them changed.

#### Scenario: Hostname changed in the file
- WHEN a push changes the entry's hostname from `old.example.org` to `new.example.org`
- THEN the deploy of that push updates the deployment, and Caddy routes `new.example.org` and no longer routes `old.example.org`

#### Scenario: Hostname that carries the webhook
- WHEN the changed hostname is the one the project's GitHub webhook points at
- THEN the webhook address is left as it was, as [github-app](github-app-spec.md) specifies, and the operator moves it

[bug] Stored aliases are read back sorted by name and compared with the file's aliases in file order. An entry listing aliases unsorted is treated as changed on every deploy: the log records `_boson.yml changed`, the names are rewritten and Caddy is reloaded.

#### Scenario: Entry removed from the file
- WHEN a deployed branch's entry is removed and the branch is pushed
- THEN the deploy fails with `_boson.yml no longer declares <branch>`
- AND the deployment's containers keep running until `boson remove`, or until its stored `expire_after` elapses
