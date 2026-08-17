import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

/// The on-disk layout of a workspace folder.
///
/// The user points this at a folder inside OneDrive (or Dropbox, or iCloud
/// Drive). Everything the app owns lives beneath it:
///
/// ```
/// <root>/
///   config.json          statuses, categories, tags, settings
///   tasks/               one JSON file per task, named by ULID
///   attachments/         copied files, prefixed by attachment id
///   .trash/              deleted tasks and their attachments
/// ```
class Workspace {
  final Directory root;

  Workspace(this.root);

  Workspace.at(String path) : root = Directory(path);

  File get configFile => File(p.join(root.path, 'config.json'));
  Directory get tasksDir => Directory(p.join(root.path, 'tasks'));
  Directory get attachmentsDir => Directory(p.join(root.path, 'attachments'));
  Directory get trashDir => Directory(p.join(root.path, '.trash'));
  Directory get trashTasksDir => Directory(p.join(trashDir.path, 'tasks'));
  Directory get trashAttachmentsDir =>
      Directory(p.join(trashDir.path, 'attachments'));

  File taskFile(String taskId) => File(p.join(tasksDir.path, '$taskId.json'));

  File attachmentFile(String storedName) =>
      File(p.join(attachmentsDir.path, storedName));

  Future<void> ensureLayout() async {
    for (final dir in [
      root,
      tasksDir,
      attachmentsDir,
      trashTasksDir,
      trashAttachmentsDir,
    ]) {
      if (!await dir.exists()) await dir.create(recursive: true);
    }
  }

  /// True when the folder already holds a might-do workspace.
  Future<bool> get isInitialised => configFile.exists();
}

/// Writes JSON that a sync client can never catch half-finished.
///
/// The write goes to a temporary file and is then renamed over the target,
/// which is atomic on every platform we ship to. Without this, OneDrive will
/// eventually upload a partially written task and you get a corrupt file
/// instead of a conflict.
Future<void> writeJsonAtomic(File file, Map<String, dynamic> json) async {
  const encoder = JsonEncoder.withIndent('  ');
  final contents = '${encoder.convert(json)}\n';

  final parent = file.parent;
  if (!await parent.exists()) await parent.create(recursive: true);

  final temp = File('${file.path}.tmp');
  await temp.writeAsString(contents, flush: true);

  try {
    await temp.rename(file.path);
  } on FileSystemException {
    // Some Windows configurations refuse a rename onto an existing file.
    // Falling back leaves a very small window where the target is absent,
    // which is still far better than a partially written file.
    if (await file.exists()) await file.delete();
    await temp.rename(file.path);
  }
}

Future<Map<String, dynamic>?> readJson(File file) async {
  if (!await file.exists()) return null;
  final contents = await file.readAsString();
  if (contents.trim().isEmpty) return null;
  return (jsonDecode(contents) as Map).cast<String, dynamic>();
}

/// Matches a filename the app itself would have written: a 26-character
/// Crockford base32 ULID (the alphabet omits I, L, O and U).
///
/// Matched case-insensitively — the ULID package emits lowercase, but a sync
/// client or a case-insensitive filesystem may hand the name back in another
/// case, and a task must never be mistaken for a foreign file over casing.
final RegExp _ulidFileName =
    RegExp(r'^[0-9A-HJKMNP-TV-Z]{26}\.json$', caseSensitive: false);

/// Whether [fileName] is a task file this app wrote.
bool isOwnTaskFile(String fileName) => _ulidFileName.hasMatch(fileName);

/// A file in `tasks/` that the app did not write.
///
/// Because task filenames are strictly ULIDs, anything else in that folder came
/// from somewhere else — and in practice that means a sync client dropped a
/// conflict copy there: OneDrive's `01J....-LAPTOP.json`, Dropbox's
/// `01J... (conflicted copy 2026-08-16).json`, iCloud's `01J... 2.json`.
///
/// These are surfaced in the app rather than ignored. Silently skipping them is
/// how you discover months later that an edit was lost.
class ConflictFile {
  final File file;

  /// The task this appears to be a copy of, when the id is recoverable.
  final String? taskId;

  final DateTime modifiedAt;

  const ConflictFile({
    required this.file,
    required this.taskId,
    required this.modifiedAt,
  });

  String get fileName => p.basename(file.path);
}

final RegExp _embeddedUlid =
    RegExp(r'([0-9A-HJKMNP-TV-Z]{26})', caseSensitive: false);

/// Scans `tasks/` for files the app didn't write.
Future<List<ConflictFile>> findConflictFiles(Workspace workspace) async {
  if (!await workspace.tasksDir.exists()) return const [];

  final conflicts = <ConflictFile>[];
  await for (final entity in workspace.tasksDir.list()) {
    if (entity is! File) continue;
    final name = p.basename(entity.path);
    if (isOwnTaskFile(name)) continue;
    if (name.endsWith('.tmp')) continue; // our own in-flight write

    final match = _embeddedUlid.firstMatch(name);
    conflicts.add(ConflictFile(
      file: entity,
      taskId: match?.group(1),
      modifiedAt: await entity.lastModified(),
    ));
  }

  conflicts.sort((a, b) => b.modifiedAt.compareTo(a.modifiedAt));
  return conflicts;
}
