# Limitations

- The database is unencrypted. Each project's GitHub keys sit in it as plain text, protected only by file permissions.

- The GitHub access token appears in the command line of the `git fetch` boson runs, where anyone listing processes on the server could read it. Fine on a single-user server, not anywhere else.

- Downgrades are not supported. An upgrade can migrate the database into a shape an older binary refuses to open, so going back means restoring `/var/lib/boson/` from a backup taken before the upgrade, or deleting the database and re-adding every project by hand.

- No licence file, which leaves the repo all rights reserved by default.
