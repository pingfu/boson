# Boson: outstanding work

Work with no implementation yet. Design detail lives in `BOSON_SPEC.md`; the running system is described by `src/Boson/` and its comments.

## Marketcanary migration

Marketcanary becomes the first project under boson.

`docker-compose.prod.yml` is **deleted from the marketcanary repo**: the project's own `docker-compose.yml` (or that file renamed) becomes what boson runs at the repo root. The `docker/Caddyfile` is also deleted, since boson generates Caddy routing. The `deploy/` directory disappears entirely.

The marketcanary repo retains:

- App code (`src/`)
- `docker/Dockerfile` (referenced by the project's compose)
- `docker-compose.yml` (the project's compose; binds to `127.0.0.1:8080:8080` so boson's Caddy can reach it)
- Migration system, jobs, etc., unchanged
- `INFRASTRUCTURE.md` collapses to "managed by boson"
- `DEPLOYMENT.md` collapses to "boson detects pushes; see boson docs"

Cutting over means standing boson up on the droplet, running `boson add` for the repo, placing `/srv/<org>/marketcanary/.env`, and running `boson deploy` before DNS moves.

## Registry mode

Specified in `BOSON_SPEC.md` §1. Marketcanary is the candidate first user: it already builds in Actions.

## Open questions

Tracked in `BOSON_SPEC.md` §3, which is authoritative.
