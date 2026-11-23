# deployment-lifecycle Specification

## Purpose

A deployment that sits idle past its `expire_after` is stopped, and the images a deployment builds are pruned to the last few.

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

[bug] When the expired deployment's hostname carries the App's webhook address, Caddy stops routing that hostname at teardown, so GitHub cannot deliver pushes for any branch of the project. If it was the project's last deployment, `boson deploy <org/name>` declares again and resets the address. Otherwise the address has to be moved by hand.

[bug] Teardown ignores the result of `docker compose down` and archives regardless. A failed Caddy synchronisation leaves the deployment archived and still routed.

#### Scenario: Status after expiry
- WHEN a deployment has expired
- THEN `boson status` no longer lists it

### Requirement: Image retention
After a successful deploy, the system SHALL keep the images of the deployment's newest `images.keep` successfully deployed commits, counting the one just deployed, and remove the images of every other commit it has deployed, including commits whose deploy failed after the image was built, with `docker image rm`. A commit that another active deployment keeps is left alone. What another deployment keeps is its own newest successful commits, counted by the `images.keep` in its own checkout's `_boson.yml`, or by the deploying branch's value when that file cannot be read, and any commit it is deploying at that moment.

The commits come from the deployment's deploy history, each counted once. Retention applies to a service that has both `build` and `image` in the resolved compose configuration and whose image tag is the deployed commit's SHA. A tag is removed when it is on disk, is 40 hex characters, and names one of the removable commits.

#### Scenario: Fourth commit with keep 3
- WHEN `images.keep` is 3 and a deployment's fourth commit deploys an image tagged `my-app:${BOSON_COMMIT}`
- THEN the tag of the oldest of the four commits is removed and the log records `removed my-app:<sha>`

#### Scenario: Two branches building one image name
- WHEN `main` and a preview branch both build `my-app:${BOSON_COMMIT}` and the preview branch deploys
- THEN the tags of commits `main` deployed are left alone, unless the preview branch deployed the same commit and neither keeps it

#### Scenario: Branches that keep different numbers
- WHEN `main` declares `images.keep: 5`, a preview branch declares `images.keep: 3`, and the preview branch deploys
- THEN none of `main`'s newest five commits is removed

#### Scenario: Deploy that built and then failed
- WHEN a deploy builds `my-app:<sha>` and `docker compose up` then exits non-zero
- THEN the next successful deploy of that deployment removes `my-app:<sha>`

#### Scenario: Commit another branch is deploying
- WHEN a commit this deployment has outlived is being deployed by another deployment at that moment
- THEN its tag is left alone

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

### Requirement: Images removed at teardown
After tearing a deployment down, the system SHALL remove the images of every commit it deployed, successfully or not, for each image its compose file builds, leaving any commit another active deployment keeps, as defined under Image retention. The image names are read from the checkout teardown leaves behind. A failure here is logged and leaves the teardown complete.

#### Scenario: Expired preview branch
- WHEN a preview deployment that built `my-app:${BOSON_COMMIT}` for three commits expires
- THEN the three tags are removed and the daemon log records each

#### Scenario: Commit shared with another branch
- WHEN the expired deployment's newest commit is also what `main` is running
- THEN that tag is left alone

#### Scenario: Compose file cannot be resolved
- WHEN the checkout is missing, or `docker compose config` fails in it
- THEN the images are left in place, the reason is logged, and the deployment stays torn down

#### Scenario: Removal fails at teardown
- WHEN `docker image rm` fails for a tag
- THEN the reason is written to the daemon log and the deployment stays torn down
