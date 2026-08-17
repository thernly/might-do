# might-do

A personal task tracker for people who find Microsoft To Do too limited but full
project-management tools too heavy. Local-first, single-user, no server and no
account. Runs on macOS, Windows and Linux.

## What a task has

A summary, a description written up front, and a running log of timestamped
notes added as work proceeds. One status, at most one category, up to ten tags,
a priority, an estimate and an actual time, a due date, an ordered list of
steps, attachments, and reminders.

## Statuses

You define your own statuses, and they are the Kanban board's columns. Each is
typed `Initial`, `Active` or `Final` — a closed set the application reasons
about. Several statuses can share a type, so `Backlog` and `Ready` are both
`Initial`, and `Done` and `Abandoned` are both `Final`.

Entering any `Final` status stamps the completion date; leaving one clears it.
Deleting a status in use is blocked until you say where its tasks should go.

## Where your data lives

You choose a folder on first run. Everything is plain files beneath it:

```
<your folder>/
  config.json       statuses, categories, tags, settings
  tasks/            one JSON file per task, named by ULID
  attachments/      copies of attached files
  .trash/           deleted tasks, never purged automatically
```

Put that folder inside OneDrive, Dropbox or iCloud Drive and your tasks follow
you between machines. Conflicts are per-task rather than whole-database, and
copies left behind by the sync client are surfaced in the app instead of being
silently ignored.

There is no database, so you can back the folder up, grep it, or read it in any
text editor — and it stays readable if might-do stops existing. See
[docs/adr/0001](docs/adr/0001-file-per-task-json-storage.md) for why not SQLite.

## Running it

```sh
flutter run -d macos      # or -d windows, -d linux
flutter test
```

## Documentation

- [CONTEXT.md](CONTEXT.md) — the domain vocabulary. Read this first.
- [docs/adr/](docs/adr/) — decisions that would otherwise look surprising.
- [docs/format/workspace-v1.md](docs/format/workspace-v1.md) — the on-disk
  format, with a conformance corpus in [fixtures/](fixtures/). What any other
  implementation is written against.

## Not in this version

Deliberately deferred, each with a chosen approach already recorded:
recurring tasks (spawn-on-complete when a task reaches a `Final` status), a
system-tray presence so reminders fire while the app is closed, sync via a
server, importing from Microsoft To Do, and code signing.

Reminders currently notify only while might-do is running. Anything that fell
due while it was closed waits in the overdue banner when you next open it.
