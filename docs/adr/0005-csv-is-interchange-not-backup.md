# CSV is interchange, not backup

might-do exports and imports a single CSV file per operation, described by
[`docs/format/csv-v1.md`](../format/csv-v1.md). The file is written for a human
reading a spreadsheet, and is **lossy by design**.

## Why not describe it as a backup

The workspace folder is already the durable artefact
([ADR-0001](0001-file-per-task-json-storage.md)): it is plain JSON, it is
greppable, and it survives might-do. Copying the folder is the backup. Nothing
in the app, the docs or the format calls CSV one.

That is not a quibble about wording. If CSV were the backup it would have to be
faithful, and a faithful CSV is a wall of ULIDs: step ids, note ids, reminder
ids, category ids, tag ids, `boardRank`. Nobody can edit that in a spreadsheet,
which destroys all three of the uses the format exists for — getting tasks out
into a spreadsheet, getting them in from another tracker, and bulk editing.

So the file carries **names** where the workspace carries ids, and the
consequences below all follow from that one decision.

## Consequences

**Import never creates a status.** A status carries a type, an order and a board
visibility that a CSV cannot express. Inventing one from a name is exactly the
guess [ADR-0002](0002-statuses-are-user-data-typed-by-a-closed-set.md) exists to
prevent, and a workspace that grew a seventh status because somebody's
spreadsheet said `Doing It Later` would be a worse outcome than a row error that
says where to add it. Unknown **categories and tags** are different: they are
names with no semantics the app reasons about, so creating them is offered as an
option, on by default.

**Import never deletes a task.** A task in the workspace with no row in the file
is left alone. The file is a set of changes, not a mirror. Making import able to
delete would mean a mis-exported filter could empty a workspace, and the app has
no undo.

**Values are written verbatim, including ones a spreadsheet reads as formulas.**
The usual mitigation for a cell beginning `=` is to prefix it with an apostrophe,
which permanently corrupts the user's own data in the user's own file. The trade
that justifies it on a web server does not exist in a single-user local-first app
writing to a path the user chose. The reader evaluates nothing.

**A column absent from the file means "nothing to say", not "empty".** This is
the rule that makes bulk editing safe: deleting the `notes` column from a
spreadsheet before importing must not delete everybody's notes. A cell that is
present and empty does clear its field.

**`boardRank` is not a column, and `completedAt` is honoured on a create only.**
The first because a hand-edited fractional index would break the invariant the
board depends on; the second because [ADR-0002](0002-statuses-are-user-data-typed-by-a-closed-set.md)
gives the status type ownership of the completion date, and the only case where
that rule loses information is a task arriving already finished from another
tool. That single exception lives inside `WorkspaceSession`, next to the rule,
rather than being handed to callers as a general-purpose setter.

## What is deliberately out of scope

Attachments (bytes do not fit in a CSV), config as its own rows (a second,
weaker definition of statuses is how two sources of truth start), the trash
(import can never write into `.trash/`), merging two workspaces (the sync client
owns cross-machine merging), and a second file format. One format, specified
once.

## Considered alternatives

**A faithful CSV with every id.** Rejected above: it satisfies a use nobody has —
the folder already does fidelity — at the cost of the three uses people do have.

**JSON-lines, or XLSX.** Both are better at fidelity than CSV and worse at the
thing that matters, which is that the file opens in the spreadsheet the user
already has. XLSX would also mean a dependency in a project that has none.

**A CSV package.** `MightDo.Core` has no third-party references, RFC 4180 is a
two-hundred-line state machine, and the fixture corpus pins it down more
precisely than a dependency's own test suite would.
