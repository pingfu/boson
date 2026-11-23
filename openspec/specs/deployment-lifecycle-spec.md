# deployment-lifecycle Specification

## Purpose

A deployment that sits idle past its `expire_after` is stopped, and after each successful deploy a deployment's own images are pruned to the last few.

## Requirements

### Requirement: Expiry sweep
The system SHALL sweep every active deployment every 15 minutes while the daemon runs, the first time 15 minutes after it starts, and tear down each whose idle time has reached its `expire_after`.

#### Scenario: Idle preview
- WHEN a deployment with `expire_after: 14d` last deployed successfully 15 days ago
- THEN the next sweep tears it down and the daemon log records `idle for 14d; stopping it`

#### Scenario: Sweep throws
- WHEN a sweep fails with an error
- THEN the error is logged and the next sweep runs 15 minutes later

#### Scenario: One deployment cannot be swept
- WHEN sweeping or tearing down one deployment throws or times out
- THEN the error is logged against that deployment and the sweep continues with the next

#### Scenario: Daemon stopping
- WHEN the daemon is stopping
- THEN the sweep ends, and this is the one cancellation that ends it

### Requirement: Duration format
The system SHALL read `expire_after` as a positive whole number, digits alone, followed by `d` (days), `h` (hours) or `m` (minutes), in either case. A deployment with no value does not expire. [boson-file](boson-file-spec.md) validation rejects a value in any other form, or one too large to be a time span.

#### Scenario: Hours
- WHEN `expire_after` is `36h`
- THEN the window is 36 hours

#### Scenario: Unreadable value
- WHEN an entry declares `expire_after: 2w`
- THEN the file fails validation and the deploy fails before it builds

#### Scenario: Unreadable value already stored
- WHEN a deployment's stored `expire_after` cannot be read
- THEN the sweep leaves that deployment running

### Requirement: Idle time
The system SHALL measure idle time from the finish of the deployment's last successful deploy, or from the deployment's creation when none has succeeded.

#### Scenario: Recent deploys all failed
- WHEN a deployment's last successful deploy is past the window and later deploys failed
- THEN the deployment expires

#### Scenario: Active branch
- WHEN a branch is pushed and deploys successfully within every window
- THEN its deployment does not expire

### Requirement: Teardown
The system SHALL tear a deployment down by running `docker compose --project-name <name> down`, archiving the deployment, which releases its hostname, aliases and port, and synchronising Caddy. The checkout stays.

#### Scenario: Deploy in flight
- WHEN the deployment's lock is held at teardown
- THEN the teardown is skipped and the next sweep considers it again

#### Scenario: Push after expiry
- WHEN an expired branch is pushed again and an entry still matches it
- THEN a new deployment is created for it, reusing the checkout directory, and the push deploys

#### Scenario: Expired deployment carried the webhook
- WHEN the expired deployment's hostname is the one the project's GitHub webhook points at
- THEN the webhook address is left as it was, as [github-app](github-app-spec.md) specifies. If it was the project's last deployment, `boson deploy <org/name>` declares again and sets the address. Otherwise the operator moves it

[bug] Teardown ignores the result of `docker compose down` and archives regardless. A failed Caddy synchronisation leaves the deployment archived and still routed.

#### Scenario: Status after expiry
- WHEN a deployment has expired
- THEN `boson status` no longer lists it

### Requirement: Image retention
After a successful deploy, the system SHALL keep the images of the deployment's newest `images.keep` successfully deployed commits, counting the one just deployed, and remove the images of its older successfully deployed commits with `docker image rm`.

Every other image is left for the operator, who clears them with `docker image prune -a`: those of failed deploys, of expired or removed deployments, and of other deployments. A deployment looks at its own history alone, so when two deployments deployed the same commit, either may remove its image once that commit is past its own `images.keep`. Docker refuses to remove an image a container is using.

The commits come from the deployment's deploy history, each counted once. Retention applies to a service that has both `build` and `image` in the resolved compose configuration and whose image tag is the deployed commit's SHA. A tag is removed when it is on disk, is 40 hex characters, and names one of the removable commits.

#### Scenario: Fourth commit with keep 3
- WHEN `images.keep` is 3 and a deployment's fourth commit deploys an image tagged `my-app:${BOSON_COMMIT}`
- THEN the tag of the oldest of the four commits is removed and the log records `removed my-app:<sha>`

#### Scenario: Two branches building one image name
- WHEN `main` and a preview branch both build `my-app:${BOSON_COMMIT}` and the preview branch deploys
- THEN the tags of commits the preview branch never deployed are left alone

#### Scenario: Commit two branches both deployed
- WHEN `main` and `develop` both deployed commit X, `develop` still keeps it, and X passes out of `main`'s newest `images.keep`
- THEN `main`'s next deploy removes X's tag unless a container is running from it, and `develop` rebuilds X if it is deployed there again

#### Scenario: Deploy that built and then failed
- WHEN a deploy builds `my-app:<sha>` and `docker compose up` then exits non-zero
- THEN `my-app:<sha>` stays on disk until the operator prunes it

#### Scenario: Expired deployment
- WHEN a deployment expires or its project is removed
- THEN the images it built stay on disk until the operator prunes them

#### Scenario: Image not tagged with the commit
- WHEN a built service's image tag is not the deployed commit
- THEN nothing is removed for that service, and the log says to tag it `:$BOSON_COMMIT` to keep the last `images.keep`

#### Scenario: Other tags on the same image
- WHEN the image name also carries tags such as `latest` or `v1.2`
- THEN those tags are left alone

#### Scenario: Published image
- WHEN a service runs an image it does not build, or one referenced by digest
- THEN retention leaves it alone

#### Scenario: Removal fails
- WHEN `docker image rm` fails for a tag
- THEN the reason is written to the deploy log and the deploy still succeeds
