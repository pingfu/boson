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
