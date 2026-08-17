import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../app/workspace_controller.dart';
import '../domain/status.dart';
import 'theme.dart';

/// Where statuses, categories and tags are managed.
class SettingsScreen extends StatelessWidget {
  final Future<void> Function() onCloseWorkspace;

  const SettingsScreen({super.key, required this.onCloseWorkspace});

  @override
  Widget build(BuildContext context) {
    return DefaultTabController(
      length: 4,
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Settings'),
          bottom: const TabBar(
            tabs: [
              Tab(text: 'Statuses'),
              Tab(text: 'Categories'),
              Tab(text: 'Tags'),
              Tab(text: 'Workspace'),
            ],
          ),
        ),
        body: TabBarView(
          children: [
            const _StatusesTab(),
            const _CategoriesTab(),
            const _TagsTab(),
            _WorkspaceTab(onCloseWorkspace: onCloseWorkspace),
          ],
        ),
      ),
    );
  }
}

class _StatusesTab extends StatelessWidget {
  const _StatusesTab();

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final config = controller.config;
    final statuses = config.orderedStatuses;
    final theme = Theme.of(context);

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  'Your statuses are the board\'s columns, in this order. Each '
                  'belongs to one of three fixed types: Initial, Active or '
                  'Final. Entering any Final status stamps the completion '
                  'date.',
                  style: theme.textTheme.bodyMedium,
                ),
              ),
              const SizedBox(width: 16),
              FilledButton.icon(
                icon: const Icon(Icons.add, size: 18),
                label: const Text('Add status'),
                onPressed: () => _editStatus(context, controller, null),
              ),
            ],
          ),
        ),
        const Divider(height: 1),
        Expanded(
          child: ReorderableListView.builder(
            itemCount: statuses.length,
            // onReorderItem already accounts for the removed item, so the
            // index needs no adjusting.
            onReorderItem: (oldIndex, newIndex) {
              final reordered = [...statuses];
              reordered.insert(newIndex, reordered.removeAt(oldIndex));
              controller.reorderStatuses(reordered);
            },
            itemBuilder: (context, index) {
              final status = statuses[index];
              final count = controller.tasksUsingStatus(status.id);
              final isDefault = status.id == config.defaultStatusId;

              return ListTile(
                key: ValueKey(status.id),
                leading: const Icon(Icons.drag_handle),
                title: Row(
                  children: [
                    Text(status.name),
                    const SizedBox(width: 8),
                    Chip(
                      label: Text(status.type.label),
                      visualDensity: VisualDensity.compact,
                      labelStyle: theme.textTheme.labelSmall,
                    ),
                    if (isDefault) ...[
                      const SizedBox(width: 6),
                      Chip(
                        label: const Text('New tasks start here'),
                        visualDensity: VisualDensity.compact,
                        labelStyle: theme.textTheme.labelSmall,
                        backgroundColor: theme.colorScheme.primaryContainer,
                      ),
                    ],
                  ],
                ),
                subtitle: Text(
                  [
                    '$count ${count == 1 ? 'task' : 'tasks'}',
                    if (status.hiddenFromBoard) 'hidden from board',
                  ].join(' · '),
                ),
                trailing: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    IconButton(
                      tooltip: status.hiddenFromBoard
                          ? 'Show column on board'
                          : 'Hide column from board',
                      icon: Icon(
                        status.hiddenFromBoard
                            ? Icons.visibility_off_outlined
                            : Icons.visibility_outlined,
                      ),
                      onPressed: () => controller.updateStatus(
                        status.copyWith(
                          hiddenFromBoard: !status.hiddenFromBoard,
                        ),
                      ),
                    ),
                    IconButton(
                      tooltip: 'Edit',
                      icon: const Icon(Icons.edit_outlined),
                      onPressed: () => _editStatus(context, controller, status),
                    ),
                    IconButton(
                      tooltip: 'Delete',
                      icon: const Icon(Icons.delete_outline),
                      onPressed: () =>
                          _deleteStatus(context, controller, status),
                    ),
                  ],
                ),
                onTap: status.type == StatusType.initial && !isDefault
                    ? () => controller.setDefaultStatus(status.id)
                    : null,
              );
            },
          ),
        ),
      ],
    );
  }

  Future<void> _editStatus(
    BuildContext context,
    WorkspaceController controller,
    Status? existing,
  ) async {
    final nameController = TextEditingController(text: existing?.name ?? '');
    var type = existing?.type ?? StatusType.initial;

    final saved = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setState) => AlertDialog(
          title: Text(existing == null ? 'New status' : 'Edit status'),
          content: SizedBox(
            width: 400,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                TextField(
                  controller: nameController,
                  autofocus: true,
                  decoration: const InputDecoration(labelText: 'Name'),
                ),
                const SizedBox(height: 16),
                DropdownButtonFormField<StatusType>(
                  initialValue: type,
                  decoration: const InputDecoration(labelText: 'Type'),
                  items: [
                    for (final value in StatusType.values)
                      DropdownMenuItem(value: value, child: Text(value.label)),
                  ],
                  onChanged: (value) {
                    if (value != null) setState(() => type = value);
                  },
                ),
                const SizedBox(height: 12),
                Text(
                  _typeHelp(type),
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(false),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.of(dialogContext).pop(true),
              child: const Text('Save'),
            ),
          ],
        ),
      ),
    );

    final name = nameController.text.trim();
    nameController.dispose();
    if (saved != true || name.isEmpty) return;

    if (existing == null) {
      await controller.addStatus(name, type);
    } else {
      await controller.updateStatus(existing.copyWith(name: name, type: type));
    }
  }

  static String _typeHelp(StatusType type) => switch (type) {
        StatusType.initial =>
          'Work not begun. Tasks can start here, and no completion date is set.',
        StatusType.active =>
          'Work under way. Blocked counts as active — it is still in flight.',
        StatusType.finalType =>
          'Work concluded. Moving a task here stamps its completion date; '
              'moving it back out clears it.',
      };

  Future<void> _deleteStatus(
    BuildContext context,
    WorkspaceController controller,
    Status status,
  ) async {
    final blocker = controller.statusDeletionBlocker(status.id);
    if (blocker != null) {
      await showDialog<void>(
        context: context,
        builder: (context) => AlertDialog(
          title: const Text('Cannot delete this status'),
          content: Text(blocker),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(context).pop(),
              child: const Text('OK'),
            ),
          ],
        ),
      );
      return;
    }

    final count = controller.tasksUsingStatus(status.id);
    final options = controller.config.orderedStatuses
        .where((s) => s.id != status.id)
        .toList();
    var reassignTo = options.first.id;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setState) => AlertDialog(
          title: Text('Delete "${status.name}"?'),
          content: SizedBox(
            width: 420,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (count == 0)
                  const Text('No tasks use this status.')
                else ...[
                  Text(
                    '$count ${count == 1 ? 'task uses' : 'tasks use'} this '
                    'status. Choose where they should go — nothing is '
                    'deleted.',
                  ),
                  const SizedBox(height: 16),
                  DropdownButtonFormField<String>(
                    initialValue: reassignTo,
                    decoration: const InputDecoration(labelText: 'Move tasks to'),
                    isExpanded: true,
                    items: [
                      for (final option in options)
                        DropdownMenuItem(
                          value: option.id,
                          child: Text('${option.name} · ${option.type.label}'),
                        ),
                    ],
                    onChanged: (value) {
                      if (value != null) setState(() => reassignTo = value);
                    },
                  ),
                ],
              ],
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(false),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.of(dialogContext).pop(true),
              child: const Text('Delete'),
            ),
          ],
        ),
      ),
    );

    if (confirmed == true) {
      await controller.deleteStatus(status.id, reassignTo: reassignTo);
    }
  }
}

class _CategoriesTab extends StatelessWidget {
  const _CategoriesTab();

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final categories = controller.config.categories;

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  'A task belongs to at most one category — the area of your '
                  'life it sits in. For cross-cutting labels, use tags.',
                  style: Theme.of(context).textTheme.bodyMedium,
                ),
              ),
              const SizedBox(width: 16),
              FilledButton.icon(
                icon: const Icon(Icons.add, size: 18),
                label: const Text('Add category'),
                onPressed: () => _edit(context, controller, null),
              ),
            ],
          ),
        ),
        const Divider(height: 1),
        Expanded(
          child: categories.isEmpty
              ? const Center(child: Text('No categories yet.'))
              : ListView.builder(
                  itemCount: categories.length,
                  itemBuilder: (context, index) {
                    final category = categories[index];
                    final count = controller.tasksUsingCategory(category.id);

                    return ListTile(
                      leading: Container(
                        width: 16,
                        height: 16,
                        decoration: BoxDecoration(
                          color: Color(category.color),
                          shape: BoxShape.circle,
                        ),
                      ),
                      title: Text(category.name),
                      subtitle: Text('$count ${count == 1 ? 'task' : 'tasks'}'),
                      trailing: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          IconButton(
                            icon: const Icon(Icons.edit_outlined),
                            onPressed: () =>
                                _edit(context, controller, category),
                          ),
                          IconButton(
                            icon: const Icon(Icons.delete_outline),
                            onPressed: () =>
                                _delete(context, controller, category, count),
                          ),
                        ],
                      ),
                    );
                  },
                ),
        ),
      ],
    );
  }

  Future<void> _edit(
    BuildContext context,
    WorkspaceController controller,
    Category? existing,
  ) async {
    final nameController = TextEditingController(text: existing?.name ?? '');
    var color = existing?.color ?? categoryPalette.first;

    final saved = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setState) => AlertDialog(
          title: Text(existing == null ? 'New category' : 'Edit category'),
          content: SizedBox(
            width: 380,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                TextField(
                  controller: nameController,
                  autofocus: true,
                  decoration: const InputDecoration(labelText: 'Name'),
                ),
                const SizedBox(height: 16),
                Wrap(
                  spacing: 8,
                  runSpacing: 8,
                  children: [
                    for (final option in categoryPalette)
                      InkWell(
                        onTap: () => setState(() => color = option),
                        child: Container(
                          width: 28,
                          height: 28,
                          decoration: BoxDecoration(
                            color: Color(option),
                            shape: BoxShape.circle,
                            border: Border.all(
                              color: option == color
                                  ? Theme.of(context).colorScheme.onSurface
                                  : Colors.transparent,
                              width: 2,
                            ),
                          ),
                        ),
                      ),
                  ],
                ),
              ],
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(false),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.of(dialogContext).pop(true),
              child: const Text('Save'),
            ),
          ],
        ),
      ),
    );

    final name = nameController.text.trim();
    nameController.dispose();
    if (saved != true || name.isEmpty) return;

    if (existing == null) {
      await controller.addCategory(name, color);
    } else {
      await controller.updateCategory(
        existing.copyWith(name: name, color: color),
      );
    }
  }

  Future<void> _delete(
    BuildContext context,
    WorkspaceController controller,
    Category category,
    int count,
  ) async {
    String? reassignTo;
    final options =
        controller.config.categories.where((c) => c.id != category.id).toList();

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setState) => AlertDialog(
          title: Text('Delete "${category.name}"?'),
          content: SizedBox(
            width: 400,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(count == 0
                    ? 'No tasks use this category.'
                    : '$count ${count == 1 ? 'task uses' : 'tasks use'} this '
                        'category. They will keep their other fields.'),
                if (count > 0 && options.isNotEmpty) ...[
                  const SizedBox(height: 16),
                  DropdownButtonFormField<String?>(
                    initialValue: reassignTo,
                    isExpanded: true,
                    decoration:
                        const InputDecoration(labelText: 'Move tasks to'),
                    items: [
                      const DropdownMenuItem<String?>(
                        value: null,
                        child: Text('No category'),
                      ),
                      for (final option in options)
                        DropdownMenuItem<String?>(
                          value: option.id,
                          child: Text(option.name),
                        ),
                    ],
                    onChanged: (value) => setState(() => reassignTo = value),
                  ),
                ],
              ],
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(false),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.of(dialogContext).pop(true),
              child: const Text('Delete'),
            ),
          ],
        ),
      ),
    );

    if (confirmed == true) {
      await controller.deleteCategory(category.id, reassignTo: reassignTo);
    }
  }
}

class _TagsTab extends StatelessWidget {
  const _TagsTab();

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final tags = controller.config.tags;

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.all(16),
          child: Text(
            'Tags are lightweight and cross-cutting. Deleting one simply '
            'detaches it from every task.',
            style: Theme.of(context).textTheme.bodyMedium,
          ),
        ),
        const Divider(height: 1),
        Expanded(
          child: tags.isEmpty
              ? const Center(
                  child: Text('No tags yet. Add them from a task.'),
                )
              : ListView.builder(
                  itemCount: tags.length,
                  itemBuilder: (context, index) {
                    final tag = tags[index];
                    final count = controller.tasks
                        .where((t) => t.tagIds.contains(tag.id))
                        .length;

                    return ListTile(
                      leading: const Icon(Icons.label_outline),
                      title: Text('#${tag.name}'),
                      subtitle: Text('$count ${count == 1 ? 'task' : 'tasks'}'),
                      trailing: IconButton(
                        icon: const Icon(Icons.delete_outline),
                        onPressed: () => controller.deleteTag(tag.id),
                      ),
                    );
                  },
                ),
        ),
      ],
    );
  }
}

class _WorkspaceTab extends StatelessWidget {
  final Future<void> Function() onCloseWorkspace;

  const _WorkspaceTab({required this.onCloseWorkspace});

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final theme = Theme.of(context);

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text('Folder', style: theme.textTheme.titleSmall),
        const SizedBox(height: 4),
        SelectableText(controller.workspace.root.path),
        const SizedBox(height: 20),
        Text('How your tasks are stored', style: theme.textTheme.titleSmall),
        const SizedBox(height: 4),
        Text(
          'Every task is one JSON file in the tasks folder, named by a unique '
          'id. Statuses, categories and tags live in config.json. Attachments '
          'are copies in the attachments folder. Deleted tasks move to .trash '
          'and are never purged automatically.\n\n'
          'Nothing here is a database, so you can back it up, grep it, or read '
          'it in any text editor — and it will still be readable if might-do '
          'stops existing.',
          style: theme.textTheme.bodyMedium,
        ),
        const SizedBox(height: 20),
        Row(
          children: [
            OutlinedButton.icon(
              icon: const Icon(Icons.refresh, size: 18),
              label: const Text('Reload from disk'),
              onPressed: controller.refresh,
            ),
            const SizedBox(width: 12),
            OutlinedButton.icon(
              icon: const Icon(Icons.folder_open, size: 18),
              label: const Text('Switch workspace'),
              onPressed: () async {
                final navigator = Navigator.of(context);
                await onCloseWorkspace();
                navigator.popUntil((route) => route.isFirst);
              },
            ),
          ],
        ),
      ],
    );
  }
}
