# boson

## Writing rules

Say what a thing is and does. Stop there. A capability statement makes its limits self-evident: "a teapot for making tea" needs no "only a teapot" and no "not also a chopping board". If a sentence exists to fence off something nobody asked about, delete it.

Banned:

- Scope disclaimers: "boson doesn't X", "never Y", "only Z", "nothing else". Exceptions: a contract an implementer must uphold ("the token is never written to disk"), or a warning with a consequence ("GitHub does not redeliver; recover with…").
- Throat-clearing: "worth stating", "worth naming", "it's important to note", "the corollary is". Just state it.
- Self-congratulation: "deliberate", "on purpose", "not a gap", "honest", "genuinely", "clean". A design described plainly reads as intentional; announcing intent is applause for the author.
- History: "earlier drafts used…", "previously…", "this replaces…". Docs describe the current design. History lives in git.
- Restating the same boundary in multiple places. State it once, where it's load-bearing; reference it elsewhere.
- Em-dashes. Use a full stop, comma, colon, or parentheses.

Test for every sentence: does an implementer or operator act on it? If it only manages the reader's impression of the design, cut it.

## Working rules

**The build has no warnings.** If a change introduces one, fix it in the same change. `dotnet build -c Release` and `dotnet test` both pass before the work is finished, and a test that needed changing is reported, not quietly rewritten to agree with the new behaviour.

**Build only what was asked for.** No flags, commands, config fields, docs sections or roadmap entries that nobody requested. A gap noticed along the way is worth one sentence in the reply, not code. Anything decided unilaterally because the request was ambiguous gets stated plainly in the reply, so it can be reversed cheaply.

**There is one consumer and no backwards compatibility.** A schema change rewrites `0001_init.sql` rather than adding a migration; a rename is a rename. The server is realigned by hand afterwards. Credentials GitHub issues once are the one exception: keep them across a change rather than making the operator recreate an App over a fixable mistake.

**Edit with the file tools.** No `sed -i`, `python`, `perl` or shell heredocs rewriting tracked files, so every change shows up as a reviewable edit.

**The config in the repository is the authority.** Where `_boson.yml` and the database disagree, the file wins and the deploy applies it. Where the file says nothing, nothing happens: no inferred cleanup, no inferred defaults, no behaviour the operator did not ask for.

**Fail before doing work, and name the fix.** A deploy that cannot succeed stops before it builds, with the path, port or setting the operator has to change in the message. Silence, or a failure discovered at runtime, is the thing being avoided.

**Comments explain why.** What the code does is readable; what it is defending against is not. A comment that restates the line above it is noise, and a workaround without its reason gets deleted by whoever meets it next.

**Verify claims on the box.** Behaviour of Docker, Caddy, systemd or the kernel gets checked over SSH or in a throwaway container before it is written down as fact, and destructive probes never run against anything real.

## Commits

Message only: no `Co-Authored-By`, no tool attribution. Subject in the imperative, body explaining why the change exists and what it costs, with measurements where a number was the reason.
