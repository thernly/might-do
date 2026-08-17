# Statuses are user data; status types are a closed set of three

Users create, rename, reorder and delete their own statuses, and those statuses
are the columns of the Kanban view. Every status is classified as exactly one of
three **status types** — `Initial`, `Active`, `Final` — and that set of three is
fixed in the application and not editable. The application reasons about types;
the user reasons about statuses.

This split exists because hardcoded Kanban columns are the limitation that
prompted this project, but a system with *no* fixed vocabulary can't behave
intelligently. The types are what let the app know that entering any `Final`
status stamps the completion date, that a new task starts in an `Initial` status,
and that a board can be meaningfully summarised — while still letting one user
run `Backlog → Ready → Doing → Review → Done` and another run `New → WIP →
Shipped`.

## Consequences

- Several statuses may share a type, and this is the normal case: `Backlog` and
  `Ready` are both `Initial`; `Done` and `Abandoned` are both `Final`. Code must
  never assume a type maps to one status.
- Completion date is derived from *type*, not from a particular status, and is
  cleared if a task moves back out of a `Final` status.
- There is no "the final status" and no "the done status". Which `Final` status a
  task lands in is a deliberate user choice — distinguishing `Done` from
  `Abandoned` is the point of allowing more than one.
- One `Initial` status is designated the default for newly created tasks. That
  designation means nothing else.
- Deleting a status in use is blocked; the user must nominate a replacement to
  move affected tasks to. Tasks are never orphaned or cascade-deleted.
- Statuses carry a flag to hide their column from the board, so a `Backlog`
  holding two hundred cards doesn't swamp columns holding five. Backlog is
  therefore an ordinary status, not a separate concept.
- Adding a fourth status type is a breaking change to every consumer of the
  taxonomy. It should be treated as a new decision, not an extension of this one.
