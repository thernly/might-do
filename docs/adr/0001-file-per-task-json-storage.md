# File-per-task JSON storage, not SQLite

might-do keeps each task as its own pretty-printed JSON file, named by ULID, in a
user-chosen folder that the user is expected to place inside OneDrive, Dropbox or
iCloud Drive. Statuses, categories, tags and settings live in a single
`config.json` beside them. Attachments are copied into a sibling folder as loose
files. We chose this over the obvious answer — a single SQLite database — because
the sync strategy rules SQLite out and makes conflicts survivable.

## Considered options

**SQLite in the synced folder.** Rejected outright: sync clients copy whole files
on their own schedule and know nothing of SQLite's locking, so the `-wal` and
`-shm` sidecars can sync out of step with the main database and produce a corrupt
file rather than a resolvable conflict. This is a documented failure mode, not a
theoretical risk.

**A single JSON document in the synced folder.** Safe from corruption, but makes
the entire dataset the unit of conflict — editing one task on each of two
machines yields a whole-file "conflicted copy" and a manual diff.

**SQLite locally, with a sync server.** Correct, and the only option that would
support sharing between people. Rejected because it means hosting,
authentication, backups and merge semantics — comfortably more work than the rest
of the application, for a tool that is deliberately single-user.

**File-per-task (chosen).** Conflicts are per-task, so the two tasks edited on
both machines conflict and the other four hundred do not. Files are plain text,
greppable, trivially backed up, and outlive the application.

## Consequences

- Queries are our code over an in-memory collection, not SQL. Acceptable at
  hundreds or low thousands of tasks; it would not be at fifty thousand.
- `config.json` is shared and therefore the one genuine conflict hotspot. Status
  and category edits are rare enough that we accept this rather than shard it.
- Writes must be atomic — temp file, then rename — so a sync client never uploads
  a half-written task, and file handles are never held open.
- The app watches the folder, live-reloads changed files, and surfaces sync
  conflict artefacts (`task-LAPTOP.json`, `(conflicted copy)`) in-app for the
  user to resolve. Without this the design silently loses data.
- ULID filenames are collision-free across machines editing offline and sort
  chronologically, so a directory listing is in creation order.
- Should this ever need to support multiple people sharing tasks, this decision
  does not extend — that requires a server and should be revisited wholesale.
