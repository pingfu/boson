# routing Specification

## Purpose

Caddy terminates TLS for every hostname and proxies each to its deployment's loopback port. boson builds Caddy's whole configuration from the database and loads it through the admin API.

## Requirements

### Requirement: Full replace
The system SHALL build the complete Caddy JSON configuration from the control hostname and every active deployment, and load it with `POST http://127.0.0.1:2019/load`. Loads from the daemon run one at a time.

#### Scenario: Two deployments change at once
- **WHEN** two deploys trigger a synchronisation at the same moment
- **THEN** the loads run one after the other, each built from the database as it then stands

#### Scenario: Caddy rejects the configuration
- **WHEN** `/load` answers with a non-success status
- **THEN** the synchronisation fails with `caddy rejected config load (<status>): <body>`

### Requirement: When routing is synchronised
The system SHALL synchronise Caddy when a deployment is created, when its declared hostname, aliases, environment set or `expire_after` change, when it is torn down, when a project is removed, and during `boson init`.

#### Scenario: New deployment
- **WHEN** a deploy creates a deployment for `feature-x.example.org`
- **THEN** Caddy routes that hostname before the build starts

### Requirement: Route order
The system SHALL emit routes in this order:

1. Control hostname with path `/_boson/*`, proxied to `127.0.0.1:9000`.
2. For each deployment, ordered by hostname:
   1. One redirect route per alias, ordered by alias.
   2. The deployment's hostname with path `/_boson/webhook/*`, proxied to `127.0.0.1:9000`.
   3. The deployment's hostname, proxied to `127.0.0.1:<port>`.
3. Control hostname, any path, answered 404.

The same database state produces a byte-identical configuration.

#### Scenario: Webhook on a deployment hostname
- **WHEN** GitHub posts to `https://example.org/_boson/webhook/acme/site`
- **THEN** the request reaches the daemon

#### Scenario: Other path on the control hostname
- **WHEN** a request for `https://<control-hostname>/anything` arrives
- **THEN** the response is 404

### Requirement: Reserved path
The system SHALL reserve `/_boson/webhook/` on every deployment hostname. Every other path reaches the deployment.

#### Scenario: Application path
- **WHEN** a request for `https://example.org/_boson/health` arrives on a deployment hostname
- **THEN** it is proxied to the deployment

### Requirement: Aliases
The system SHALL answer every request to an alias with a 308 redirect to `https://<hostname><original URI>`.

#### Scenario: www alias
- **WHEN** `www.example.org` is an alias of `example.org` and a request for `https://www.example.org/a?b=1` arrives
- **THEN** the response is 308 with `Location: https://example.org/a?b=1`

### Requirement: Compression
The system SHALL apply gzip response encoding on every proxied route.

#### Scenario: Compressible response
- **WHEN** a client that accepts gzip requests a text response from a deployment
- **THEN** Caddy compresses it

### Requirement: HTTPS
The system SHALL configure one HTTP server listening on `:443`, leaving port 80 to Caddy's automatic HTTPS for redirects and ACME HTTP-01 challenges.

#### Scenario: Plain HTTP request
- **WHEN** a request for `http://example.org/` arrives
- **THEN** Caddy redirects it to HTTPS

### Requirement: Certificates
The system SHALL issue certificates for deployment hostnames and aliases through Caddy's default ACME issuers. For the control hostname, the system SHALL configure the ACME issuer followed by Caddy's internal issuer.

#### Scenario: Private control hostname
- **WHEN** the control hostname is a name no public CA will issue for
- **THEN** Caddy serves it with a certificate from its internal CA

#### Scenario: ACME fails for a deployment hostname
- **WHEN** ACME issuance fails for a deployment's hostname
- **THEN** no internally issued certificate is served for it, and Caddy keeps retrying

### Requirement: Name uniqueness
The system SHALL hold each hostname and alias for at most one active deployment, compared case-insensitively. A deployment whose names collide with another's is refused.

#### Scenario: Hostname already served
- **WHEN** a deploy would give a deployment a hostname or alias another active deployment holds
- **THEN** the change is refused with a message naming the hostname, and nothing is built

#### Scenario: Name released
- **WHEN** a deployment is archived
- **THEN** its hostname and aliases can be claimed by another deployment

### Requirement: Configuration survives a Caddy restart
The system SHALL run Caddy with `caddy run --resume`, so a restarted container reloads the last configuration it was given.

#### Scenario: Container restart
- **WHEN** the `boson-caddy` container restarts
- **THEN** it serves every hostname it served before, with no action from boson

### Requirement: Logs
The system SHALL configure Caddy to write access and runtime logs as JSON to stdout at level INFO, read with `docker logs boson-caddy`.

#### Scenario: Diagnosing certificate issuance
- **WHEN** the operator runs `docker logs boson-caddy`
- **THEN** the output includes Caddy's ACME activity for each hostname
