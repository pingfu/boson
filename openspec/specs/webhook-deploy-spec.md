# webhook-deploy Specification

## Purpose

GitHub delivers push events to `POST /_boson/webhook/<org>/<name>`. A verified push to a branch starts a deploy of that branch.

## Requirements

### Requirement: Validation pipeline
The system SHALL validate each delivery in this order and answer at the first step that decides it:

| Step | Condition | Response |
|---|---|---|
| 1 | Body exceeds 25 MB | 413 |
| 2 | No active project for the repository in the path | 404 |
| 3 | `X-Hub-Signature-256` does not verify | 403 |
| 4 | `X-GitHub-Event` is `ping` | 200 `{"status":"pong"}` |
| 5 | Body is not JSON with a string `ref` | 400 |
| 6 | `ref` does not start with `refs/heads/` | 200 `{"status":"ignored"}` |
| 7 | The branch's deployment has not been activated | 200 `{"status":"inactive"}`, or 202 `{"status":"queued"}` when its first deploy is running |
| 8 | Otherwise | 202 `{"status":"queued"}` |

The repository in the path is lowercased before lookup. Any signed event other than `ping` whose body has a branch `ref` is treated as a push. A 403 means the signature failed. A 200 or 202 means it verified. A 202 means a deploy was queued, and its outcome is in `boson status` and the deploy log.

#### Scenario: Push to a deployed branch
- WHEN a signed push to `refs/heads/main` arrives for a project whose `main` deployment is active
- THEN the response is 202 `{"status":"queued"}` and a deploy of `main` starts with trigger `webhook`

#### Scenario: Tag push
- WHEN a signed push to `refs/tags/v1` arrives
- THEN the response is 200 `{"status":"ignored"}` and nothing is deployed

#### Scenario: Removed project
- WHEN a delivery arrives for an archived project
- THEN the response is 404

[bug] A signed body that is valid JSON but not an object, such as `[]`, is answered 500 instead of 400, with no log line.

### Requirement: Signature verification
The system SHALL compute HMAC-SHA256 over the raw request body with the project's webhook secret and compare it, in constant time, with the hex value after `sha256=` in `X-Hub-Signature-256`. The body is parsed after the signature verifies.

#### Scenario: Wrong secret
- WHEN the signature was computed with a different secret
- THEN the response is 403

#### Scenario: Header missing or malformed
- WHEN the header is absent, lacks the `sha256=` prefix, or is not hex
- THEN the response is 403

### Requirement: Response before work
The system SHALL start the deploy on a background task and respond without waiting for it.

#### Scenario: Long build
- WHEN a push starts a deploy whose build takes several minutes
- THEN GitHub receives the 202 immediately

### Requirement: First push to a new branch
The system SHALL start a deploy for a verified push to a branch that has no deployment. The deploy decides from `_boson.yml` whether the branch deploys, as specified in [deploy](deploy-spec.md).

#### Scenario: Branch matches a pattern
- WHEN `feature/x` is pushed for the first time and a pattern entry matches it
- THEN the response is 202 and the deploy creates the deployment

#### Scenario: Branch matches nothing
- WHEN a branch no entry matches is pushed
- THEN the response is 202 and the deploy ends without creating anything

### Requirement: Activation filter
The system SHALL decline pushes to a deployment whose `webhook_active` is unset. Activation happens on the deployment's first successful deploy.

#### Scenario: Deployment declared, never deployed
- WHEN a push arrives for a deployment created by `boson add` that has not yet deployed successfully
- THEN the response is 200 `{"status":"inactive"}` and nothing is deployed

#### Scenario: Push during the first deploy
- WHEN a push arrives for an inactive deployment while its first deploy holds the lock
- THEN `deploy_pending` is set and the response is 202 `{"status":"queued"}`

#### Scenario: Push-created deployment whose first deploy failed
- WHEN a pattern branch's first deploy fails and the fix is pushed
- THEN the response is 200 `{"status":"inactive"}`, and the branch deploys again once `boson deploy <org/name> --branch <name>` succeeds

### Requirement: Coalescing
The system SHALL record a push that arrives while its branch is deploying by setting `deploy_pending` on the deployment. The running deploy drains it as specified in [deploy](deploy-spec.md), which also lists the cases where the push is lost.

#### Scenario: Three pushes during one deploy
- WHEN three pushes to `main` arrive while `main` is deploying
- THEN one further pass deploys the latest tip

### Requirement: Delivery logging
The system SHALL log every delivery it answers with the repository, the status code and the reason. A body over 26 MB is cut off by the listener and leaves no line.

#### Scenario: Signature failure
- WHEN a delivery is answered 403
- THEN the daemon log holds `webhook <org/name>: 403 signature verification failed`

### Requirement: Recovery from a missed delivery
GitHub does not redeliver a webhook on its own. A missed push to a branch that has a deployment is recovered with `boson deploy <org/name> --branch <name>`, which deploys the branch tip. A branch with no deployment, such as a new pattern branch or an expired one, is refused by `boson deploy` and recovered by another push or by redelivering from GitHub's webhook page.

#### Scenario: Daemon down during a push
- WHEN a push to a deployed branch is delivered while the daemon is stopped
- THEN the branch is not deployed until the next push or a manual `boson deploy`
