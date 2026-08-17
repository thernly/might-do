import 'dart:io';

import 'package:file_selector/file_selector.dart';
import 'package:flutter/material.dart';
import 'package:path/path.dart' as p;

import '../storage/workspace.dart';

/// First-run screen: choose where the tasks live.
///
/// The folder has to be chosen rather than assumed, because the whole
/// multi-device story rests on it sitting inside OneDrive, Dropbox or iCloud
/// Drive — and only the user knows where that is on this machine.
class WorkspacePicker extends StatefulWidget {
  final String? rememberedPath;
  final Future<void> Function(String path) onChosen;

  const WorkspacePicker({
    super.key,
    required this.rememberedPath,
    required this.onChosen,
  });

  @override
  State<WorkspacePicker> createState() => _WorkspacePickerState();
}

class _WorkspacePickerState extends State<WorkspacePicker> {
  bool _busy = false;
  String? _error;

  Future<void> _choose() async {
    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final path = await getDirectoryPath(
        confirmButtonText: 'Use this folder',
      );
      if (path == null) {
        setState(() => _busy = false);
        return;
      }

      final directory = Directory(path);
      if (!await directory.exists()) {
        setState(() {
          _busy = false;
          _error = 'That folder no longer exists.';
        });
        return;
      }

      await widget.onChosen(path);
    } catch (error) {
      if (mounted) {
        setState(() {
          _busy = false;
          _error = '$error';
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final remembered = widget.rememberedPath;

    return Scaffold(
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 520),
          child: Padding(
            padding: const EdgeInsets.all(32),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('might-do', style: theme.textTheme.headlineMedium),
                const SizedBox(height: 8),
                Text(
                  'Choose a folder for your tasks.',
                  style: theme.textTheme.titleMedium,
                ),
                const SizedBox(height: 16),
                Text(
                  'Everything is stored as plain files in this folder — one per '
                  'task. Put it inside OneDrive, Dropbox or iCloud Drive and '
                  'your tasks follow you between machines. No account, no '
                  'server.',
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                ),
                if (remembered != null) ...[
                  const SizedBox(height: 20),
                  Card(
                    child: Padding(
                      padding: const EdgeInsets.all(12),
                      child: Row(
                        children: [
                          Icon(Icons.error_outline,
                              color: theme.colorScheme.error),
                          const SizedBox(width: 12),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(
                                  'Your last workspace is missing',
                                  style: theme.textTheme.titleSmall,
                                ),
                                const SizedBox(height: 2),
                                Text(
                                  remembered,
                                  style: theme.textTheme.bodySmall?.copyWith(
                                    color: theme.colorScheme.onSurfaceVariant,
                                  ),
                                ),
                                const SizedBox(height: 4),
                                Text(
                                  'It may be on a drive that is not mounted, or '
                                  'still syncing. Your tasks are not lost.',
                                  style: theme.textTheme.bodySmall,
                                ),
                              ],
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                ],
                if (_error != null) ...[
                  const SizedBox(height: 16),
                  Text(
                    _error!,
                    style: theme.textTheme.bodyMedium
                        ?.copyWith(color: theme.colorScheme.error),
                  ),
                ],
                const SizedBox(height: 24),
                Row(
                  children: [
                    FilledButton.icon(
                      onPressed: _busy ? null : _choose,
                      icon: const Icon(Icons.folder_open),
                      label: Text(
                        remembered == null
                            ? 'Choose folder'
                            : 'Choose another folder',
                      ),
                    ),
                    if (_busy) ...[
                      const SizedBox(width: 16),
                      const SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      ),
                    ],
                  ],
                ),
                const SizedBox(height: 12),
                Text(
                  'Suggestion: ${p.join(_homeHint(), 'OneDrive', 'might-do')}',
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  String _homeHint() {
    final env = Platform.environment;
    return env['HOME'] ?? env['USERPROFILE'] ?? '~';
  }
}

/// Whether [path] already holds a workspace, used to word the picker's
/// confirmation.
Future<bool> folderHasWorkspace(String path) =>
    Workspace.at(path).isInitialised;
