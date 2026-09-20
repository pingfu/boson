# Roadmap

- **Let GitHub Actions build the images.** Collect image builds from GitHub Actions rather than on the Boson server, because today git pull and build on server competes with every running site for processor, memory and disk, and leftover build files pile up until manually cleared. A project would opt in by naming a ready-built image in its compose file; boson then deploys when a build finishes successfully rather than when someone pushes, ties each image to the exact commit it came from so "put commit X back" is a complete instruction, and signs in to GitHub's image store with credentials it already holds for that project. A project with just a Dockerfile keeps working the way it does now.

- **Let a project name the build boson watches for.** Take the workflow filename from the project rather than fixing it at `.github/workflows/deploy.yml`, because today a repository that already has its own workflow layout has to rename a file to suit boson. A project would name the build it wants watched when it is added, falling back to the current filename when it says nothing.

- **Make running deploys visible in boson status cli output.** Surface an in-progress deploy as its own status signal rather than burying it in the last-deploy text, because during a slow build the operator wants to know whether boson is working, stuck, or idle before opening the deploy log.

- **Snapshot tests for the Caddy config and the systemd unit.** Compare generated config against a stored copy rather than against expectations written inline, because today both outputs are long enough that an unintended change is easy to miss by eye. A change would show up as a difference to approve or reject.
