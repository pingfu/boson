# deployment-lifecycle Specification

## Purpose

A deployment that sits idle past its `expire_after` is stopped, and the images a deployment builds are pruned to the last few.

## Requirements

### Requirement: Expiry sweep
The system SHALL sweep every active deployment every 15 minutes while the daemon runs, and tear down each whose idle time has reached its `expire_after`.

#### Scenario: Idle preview
- WHEN a deployment with `expire_after: 14d` last deployed successfully 15 days ago
- THEN the next sweep tears it down and the daemon log records `idle for 14d; stopping it`

#### Scenario: Sweep throws
- WHEN a sweep fails with an error
- THEN the error is logged and the next sweep runs 15 minutes later

### Requirement: Duration format
The system SHALL read `expire_after` as a positive integer followed by `d` (days), `h` (hours) or `m` (minutes), in either case. A deployment with no value, or a value in any other form, does not expire.

#### Scenario: Hours
- WHEN `expire_after` is `36h`
- THEN the window is 36 hours

#### Scenario: Unreadable value
- WHEN `expire_after` is `2w`
- THEN the deployment does not expire

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

[bug] When the expired deployment's hostname carries the App's webhook address, Caddy stops routing that hostname at teardown, so GitHub cannot deliver the push and no branch of the project deploys until the address is moved by hand.

#### Scenario: Status after expiry
- WHEN a deployment has expired
- THEN `boson status` no longer lists it

### Requirement: Image retention
After a successful deploy, the system SHALL keep the newest `images.keep` commit-tagged builds of each image the deployment builds, counting the one just deployed, and remove the older ones with `docker image rm`.

Retention applies to a service that has both `build` and `image` in the resolved compose configuration and whose image tag is the deployed commit's SHA. Among the image's existing tags, those that are 40 hex characters are candidates for removal.

#### Scenario: Fourth build with keep 3
- WHEN `images.keep` is 3 and a fourth commit deploys an image tagged `my-app:${BOSON_COMMIT}`
- THEN the tag of the oldest of the four commits is removed and the log records `removed my-app:<sha>`

#### Scenario: Image not tagged with the commit
- WHEN a built service's image tag is not the deployed commit
- THEN nothing is removed, and the log says to tag it `:$BOSON_COMMIT` to keep the last `images.keep`

#### Scenario: Other tags on the same image
- WHEN the image name also carries tags such as `latest` or `v1.2`
- THEN those tags are left alone

#### Scenario: Published image
- WHEN a service runs an image it does not build, or one referenced by digest
- THEN retention leaves it alone

#### Scenario: Removal fails
- WHEN `docker image rm` fails for a tag
- THEN the reason is written to the deploy log and the deploy still succeeds
