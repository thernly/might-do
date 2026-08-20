# The might-do CSV interchange format, version 1

This describes the file might-do writes when you export tasks, and reads when
you import them. It is a **separate format from
[`workspace-v1`](workspace-v1.md) and a much weaker promise**: the workspace
folder is the durable artefact and the thing to copy for a backup, and this is a
view of it, shaped for a spreadsheet.

CSV exists here for three things the folder cannot do — getting tasks out into a
spreadsheet to sort, pivot and share; getting tasks in from another tracker; and
bulk editing forty tasks in a spreadsheet rather than clicking through forty
detail panes. Every decision below follows from those, and from
[ADR-0005](../adr/0005-csv-is-interchange-not-backup.md).

The machine-readable half of this document is
[`fixtures/csv-v1/`](../../fixtures/csv-v1/). Where prose and fixtures disagree,
the fixtures win.

## Physical shape

What might-do **writes**:

- **RFC 4180.** Comma delimiter, `"` quoting, doubled `""` for a literal quote.
  A field is quoted when it contains a comma, a quote or a newline, and left bare
  otherwise.
- **UTF-8 with a BOM.** The BOM is there for exactly one reason: Excel on Windows
  reads a UTF-8 CSV as the local codepage without it, and `Café` arriving as
  `CafÃ©` in the first thing you do with the feature is not acceptable.
- **CRLF line endings**, including the last line, which gets a trailing break.
- **One header row**, with the column names below, in that order.
- **Embedded newlines are LF**, inside a quoted cell — used by `description`,
  `steps` and `notes`.

What might-do **reads** is deliberately more liberal, in the same spirit as the
JSON reader:

- BOM present or absent.
- LF, CRLF or CR line endings.
- Delimiter sniffed from the header line: comma, semicolon or tab. Semicolon is
  what a European Excel writes, and refusing it would be a support burden with no
  upside.
- Columns in any order; unknown columns ignored; absent optional columns take
  their documented default.
- Header matching is case-insensitive and ignores spaces, underscores and
  hyphens, so `Due Date`, `dueDate` and `due_date` are the same column.
- A trailing blank line, or a row of nothing but empty cells, is skipped rather
  than read as an empty task.

## Columns

| Column | Type | Export | Import |
|---|---|---|---|
| `id` | ULID | Always written | Blank → create. Known → update. Unknown → create with that id |
| `summary` | text | | Required and non-blank on a create; blank is a row error on an update too |
| `description` | text, may contain newlines | | |
| `status` | status **name** | | Required; must already exist |
| `statusType` | `initial`/`active`/`final` | Written | **Ignored.** Informational only |
| `category` | category **name** | | Blank clears; unknown name is created if the option is on |
| `tags` | names, `;`-separated | | Blank clears; unknown names created if the option is on; capped at 10 |
| `priority` | `low`/`medium`/`high`/`critical` | | Default `medium` |
| `dueDate` | `yyyy-MM-dd` | | Blank clears |
| `completedAt` | ISO-8601 instant, UTC | Written | Honoured **only** when creating a task directly into a `final` status; ignored otherwise |
| `estimateMinutes` | int ≥ 0 | | Blank clears |
| `totalTimeMinutes` | int ≥ 0 | | Blank clears |
| `steps` | grammar below | | |
| `notes` | grammar below | | |
| `reminders` | ISO instants, `;`-separated | | Only pending reminders round-trip |
| `attachments` | original names, `;`-separated | Written | **Ignored** |
| `createdAt` | ISO-8601 instant, UTC | Written | Honoured on create; ignored on update |
| `updatedAt` | ISO-8601 instant, UTC | Written | **Ignored** — stamped by the session |

`boardRank` is **not a column**. It is a fractional index that means nothing to a
spreadsheet user and everything to the board; a hand-edited value would break the
"no rank ends in `0`" invariant the format depends on. Existing tasks keep the
rank they have because import never touches it; imported new tasks go to the
bottom of their column, exactly as creating one in the app does.

`schemaVersion` is not a column either. The CSV's version is the header row: if
`csv-v2` ever exists it will add or rename columns, and a reader that matches
columns by name and ignores what it does not know handles that without a number
to compare.

### Blank versus missing

These are different, and the distinction is what makes bulk editing safe:

- A **column absent from the file** means "this file has nothing to say about
  this field". On an update the existing value is kept. Deleting the `notes`
  column from a spreadsheet before importing must not delete everybody's notes.
- A **cell that is present and empty** means "no value": it clears an optional
  field and, for `summary` or `status`, is a row error.

### `steps`

One step per line inside one cell, GitHub-checkbox shaped, because it is the
notation the audience already reads:

```
[x] Draft the outline
[ ] Circulate for comment
[ ] Send
```

`[x]` (or `[X]`) is done, `[ ]` and `[]` are not, and a line with no marker at
all is a step that is not done — someone typing three lines into a cell gets
three steps, which is the point.

**Step ids are not written, and round-tripping does not churn them.** On import,
a step keeps the existing task's step id when the text at that position is
unchanged; anything else mints a new ULID. Exporting and re-importing an
untouched task therefore produces no write at all, and moving one line in a
spreadsheet renumbers only what moved.

### `notes`

One note per line, `<instant>` TAB `<body>`:

```
2026-08-14T09:12:00Z	Rang the supplier, waiting on a quote
2026-08-16T15:40:11Z	Quote arrived, over budget
```

A tab rather than a space or a comma: the body is prose, and prose contains
commas and dashes but almost never a literal tab. A line with no tab is a note
whose timestamp is the moment of import — again so that typing prose into the
cell works.

Notes are append-only in the app and are treated that way here: a note matching
an existing one on **both timestamp and body** keeps its id; anything else is a
new note. Deleting a line does delete the note, because refusing would make the
column a lie, but the preview counts note deletions separately and prominently.

A note or step whose own text contains a line break would make the cell's line
count stop matching its item count, and the cell unparseable — so inside these
two cells, and only these two, a newline is written `\n`, a tab `\t`, and a
literal backslash `\\`. They are read back the same way.

### `reminders`

Semicolon-separated ISO-8601 instants, and **only the pending ones** — a fired or
dismissed reminder is bookkeeping about a notification that has already happened,
and re-importing it would either resurrect a dismissed alert or silently drop the
`firedAt` that stops it firing twice. Existing fired and dismissed reminders on a
task being updated are left exactly as they are; the cell governs pending ones
only. This is the one place the CSV is a **view of part of a field**.

### Values are written verbatim

A cell beginning `=`, `+`, `-` or `@` is written as it stands. Spreadsheets treat
a leading `=` as a formula, and the usual mitigation is to prefix the value with
a quote or an apostrophe — which corrupts your own data in your own file,
permanently, on the way out.

The trade is different here than on a web server: this is a single-user
local-first app writing your own tasks to a path you chose. A task summary that
starts with `=` is a summary that starts with `=`. The import reader never
evaluates anything it reads.

## What import refuses

Row-level, not file-level: one bad date on line 40 does not refuse the other 200
rows, and each error carries the **line number in the file**, because that is
what you can go and look at in your spreadsheet.

- `summary` missing or blank on a create.
- `status` missing, blank, or naming a status that does not exist. **Import never
  creates a status.** A status carries a type, an order and a board visibility
  that a CSV cannot express, and inventing one silently is exactly the kind of
  guess [ADR-0002](../adr/0002-statuses-are-user-data-typed-by-a-closed-set.md)
  exists to prevent. The message says which name, and that it must be added in
  Settings first.
- An unparseable `priority`, `dueDate`, `completedAt`, `createdAt`, `reminders`
  entry, or a negative or non-integer `estimateMinutes`/`totalTimeMinutes`.
- An `id` that is not a ULID.
- A **duplicate `id` within the file** — both rows are errors, because there is
  no honest way to choose which one wins.
- An `id` that belongs to a **trashed** task. The message says to restore it
  first; quietly recreating it beside its own trashed copy is how you end up with
  two.

Whole-file refusals are kept to the cases where per-row recovery is meaningless:
no header row, no recognisable columns, or a file over **16 MB** — the same limit
and the same reasoning as the workspace's own.

Unknown **categories** and **tags** are not errors. They are names with no
semantics the app reasons about, and a checkbox — *Create categories and tags
this file mentions*, on by default — governs whether they are created or the
field is left unset for that row.

A task in the workspace that has **no row in the file is left alone**. Import is
never a deletion: the file is a set of changes, not a mirror.

## Lossiness

**Export is not a backup.** A round trip preserves:

- Every scalar field, and the identity of every task.
- Steps, with their done state and their ids.
- Notes, with their timestamps and their ids.
- Pending reminders.

A round trip **loses**:

| Lost | Why |
|---|---|
| Attachments | Bytes do not fit in a CSV |
| Fired and dismissed reminders | Notification bookkeeping, not task data |
| Board position of newly-created tasks | `boardRank` is not a column |
| Statuses, categories and tags with no task using them | Nothing has a row to mention them |
| A task's `updatedAt` | Restamped by the session for rows that changed |

One consequence worth stating rather than leaving to be discovered: because
`completedAt` is ignored on an update, someone migrating from another tool who
imports the same file **twice** will find the second import's completion dates
ignored. The status rule owns that field once a task exists.
