# `csv-v1` fixtures

The specification of the interchange format described in
[`docs/format/csv-v1.md`](../../docs/format/csv-v1.md), as files rather than as
prose.

| Folder | What it pins down |
|---|---|
| `export/` | `workspace-v1.csv` is the whole [`workspace-v1`](../workspace-v1) corpus exported, byte for byte. Re-importing it into that workspace must produce no write at all |
| `tolerance/` | Files a human or another tool could plausibly produce, which the reader must accept: semicolons, no BOM, LF and CR endings, reordered and mixed-case headers, unknown columns, absent optional columns, a trailing blank line, and a cell carrying commas, quotes and newlines |
| `errors/` | One file whose every row is a different documented row error, plus one row that must survive them all |

The expected outcome for each input lives in
`tests/MightDo.Core.Tests/CsvFormatTests.cs` and `CsvImportTests.cs` rather than
in a second set of files here: unlike the workspace corpus these fixtures are
not shared with another implementation, so a second serialisation of the answer
would be a thing to keep in step for no reader's benefit.
