# might-do

A personal task tracker for people who find Microsoft To Do too limited but full
project-management tools too heavy. Deliberately single-user and local-first.

## Language

### Where tasks live

**Workspace**:
A folder holding one self-contained set of tasks, with its own statuses,
categories and tags. A user may keep several — work in one, home in another —
and the app has exactly one open at a time; switching closes the one being
left. Everything in the glossary below belongs to a workspace and means nothing
outside it: two workspaces may both have a `Blocked` status, and they are not
the same status.
_Avoid_: Project (means Category to a user, and both are wrong), vault, space,
database, account

### The task itself

**Task**:
A single unit of work the user intends to do, tracked from conception to
completion. The central concept — everything else in this glossary hangs off it.
_Avoid_: Item, todo, ticket, issue, card

**Summary**:
The task's short one-line name, used as its label everywhere it appears.
_Avoid_: Title, name, subject

**Description**:
A detailed statement of what the task involves and why it exists, written before
work starts and edited rarely.
_Avoid_: Details, body, content

**Note**:
A dated entry in a task's running commentary, recorded while work is in progress
or on completion. A task accumulates many notes over its life; they are never
edited to reflect a later understanding. Distinct from the Description, which is
written once, up front.
_Avoid_: Comment, update, log entry

### Classification

**Status**:
The stage a task has reached, named by the user. The set of statuses is
user-defined, ordered, and doubles as the columns of the Kanban view.
_Avoid_: State, stage, column, phase

**Status Type**:
The fixed classification every status belongs to — `Initial`, `Active`, or
`Final`. Users invent, rename, and reorder statuses freely, but each status is
one of these three types, and the type is what the application reasons about.
Several statuses may share a type: `Backlog` and `Ready` are both `Initial`;
`Blocked` and `In Review` are both `Active`; `Done` and `Abandoned` are both
`Final`. The set of three is closed and not user-editable.
_Avoid_: Category (means something else here), kind, class, bucket, state

**Category**:
A user-defined grouping *within a workspace* that answers "what area of my life
does this belong to?" A task has exactly one, or none. Not to be confused with
the Workspace, which is the coarser split and a different folder entirely.
_Avoid_: Project, list, folder, bucket, area

**Tag**:
A user-defined label attached to a task for cross-cutting concerns that don't
fit a single Category. A task may carry several.
_Avoid_: Label, keyword, marker

**Priority**:
How important a task is relative to others, on a fixed scale of `Low`, `Medium`,
`High`, `Critical`.
_Avoid_: Severity, importance, urgency, rank

### Attached to a task

**Step**:
A single tickable line of text belonging to a task, one of an ordered sequence
that breaks the task down. Deliberately *not* a task: it has no status, no
dates, and never appears on the Kanban view. Steps cannot contain steps, and
ticking them all off does not complete the task.
_Avoid_: Sub-task, checklist item, child task

**Attachment**:
A file copied into might-do's own storage and bound to a task. The copy is
authoritative — moving or deleting the user's original has no effect on it.
_Avoid_: File, upload, link, document

**Estimate**:
How long the user expects a task to take, recorded before work starts.
_Avoid_: Effort, points, size

**Total Time**:
How long the task actually took, recorded by hand at completion. Its value is in
the comparison against the Estimate.
_Avoid_: Actual, time spent, duration, logged time

**Reminder**:
A request for the application to notify the user about a task at a given moment.
A reminder carries its own date and time, set independently of the task's due
date; a task may have several.

### Dates

**Due Date**:
The calendar day by which the user intends to finish a task. A day, never a time
of day — a task is due on the 21st, not at 17:00 on the 21st.
_Avoid_: Deadline, target date, do date

**Completion Date**:
The moment a task entered a status of type `Final`. Set by the application, not
the user, and cleared if the task leaves that status.
_Avoid_: Done date, closed date, finished date

### Views

**List View**:
The presentation of tasks as a flat, sortable, filterable list.

**Kanban View**:
The presentation of tasks as cards in columns, where each column is a Status and
moving a card between columns changes that task's status.
_Avoid_: Board view, swimlane view
