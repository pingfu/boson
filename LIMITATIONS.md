# Limitations

- The database is unencrypted. Each project's GitHub keys sit in it as plain text, protected only by file permissions.

- The GitHub access token appears in the command line of the `git fetch` boson runs, where anyone listing processes on the server could read it. Fine on a single-user server, not anywhere else.

- Nothing deletes old images, so disk use grows with every deploy.

- No licence file, which leaves the repo all rights reserved by default.
