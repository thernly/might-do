import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../app/app_settings.dart';
import '../app/task_query.dart';
import '../app/workspace_controller.dart';
import 'board_view.dart';
import 'filter_bar.dart';
import 'reminders.dart';
import 'settings_screen.dart';
import 'task_detail_pane.dart';
import 'task_list_view.dart';

enum ViewMode { list, board }

/// The application shell: a toolbar, one of the two views, and a detail pane.
///
/// Master-detail rather than a modal editor — on a desktop-sized window you can
/// keep the list in view while editing, which is the whole reason to build a
/// desktop app rather than reuse a phone layout.
class HomeScreen extends StatefulWidget {
  final Future<void> Function() onCloseWorkspace;

  const HomeScreen({super.key, required this.onCloseWorkspace});

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  final TextEditingController _search = TextEditingController();
  final FocusNode _searchFocus = FocusNode();

  ViewMode _mode = ViewMode.list;
  TaskQuery _query = const TaskQuery();
  String? _selectedTaskId;
  bool _showFilters = false;

  @override
  void initState() {
    super.initState();
    final settings = context.read<AppSettings>();
    _mode = settings.viewMode == 'board' ? ViewMode.board : ViewMode.list;
  }

  @override
  void dispose() {
    _search.dispose();
    _searchFocus.dispose();
    super.dispose();
  }

  void _setMode(ViewMode mode) {
    setState(() => _mode = mode);
    context.read<AppSettings>().setViewMode(mode.name);
  }

  Future<void> _createTask() async {
    final controller = context.read<WorkspaceController>();
    final task = await controller.createTask(summary: 'New task');
    if (!mounted) return;
    setState(() => _selectedTaskId = task.id);
  }

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final theme = Theme.of(context);

    if (controller.isLoading) {
      return const Scaffold(body: Center(child: CircularProgressIndicator()));
    }

    final selected = _selectedTaskId == null
        ? null
        : controller.taskById(_selectedTaskId!);

    // A selected task that vanished — deleted here, or removed on another
    // machine and picked up by the watcher.
    if (_selectedTaskId != null && selected == null) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) setState(() => _selectedTaskId = null);
      });
    }

    return Scaffold(
      body: Column(
        children: [
          _Toolbar(
            mode: _mode,
            onModeChanged: _setMode,
            search: _search,
            searchFocus: _searchFocus,
            onSearchChanged: (value) =>
                setState(() => _query = _query.copyWith(search: value)),
            filtersOpen: _showFilters,
            filterCount: _activeFilterCount(),
            onToggleFilters: () =>
                setState(() => _showFilters = !_showFilters),
            onNewTask: _createTask,
            onOpenSettings: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => ChangeNotifierProvider.value(
                  value: controller,
                  child: SettingsScreen(onCloseWorkspace: widget.onCloseWorkspace),
                ),
              ),
            ),
          ),
          if (_showFilters)
            FilterBar(
              query: _query,
              config: controller.config,
              onChanged: (query) => setState(() => _query = query),
            ),
          const RemindersBanner(),
          if (controller.conflicts.isNotEmpty)
            _ConflictBanner(count: controller.conflicts.length),
          if (controller.failures.isNotEmpty)
            _FailureBanner(count: controller.failures.length),
          const Divider(height: 1),
          Expanded(
            child: Row(
              children: [
                Expanded(
                  child: _mode == ViewMode.list
                      ? TaskListView(
                          query: _query,
                          selectedTaskId: _selectedTaskId,
                          onSelect: (task) =>
                              setState(() => _selectedTaskId = task.id),
                        )
                      : BoardView(
                          query: _query,
                          selectedTaskId: _selectedTaskId,
                          onSelect: (task) =>
                              setState(() => _selectedTaskId = task.id),
                        ),
                ),
                if (selected != null) ...[
                  const VerticalDivider(width: 1),
                  SizedBox(
                    width: 440,
                    child: TaskDetailPane(
                      key: ValueKey(selected.id),
                      task: selected,
                      onClose: () => setState(() => _selectedTaskId = null),
                    ),
                  ),
                ],
              ],
            ),
          ),
          _StatusBar(
            total: controller.tasks.length,
            showing: _query.apply(controller.tasks, controller.config).length,
            style: theme.textTheme.bodySmall,
          ),
        ],
      ),
    );
  }

  int _activeFilterCount() {
    var count = 0;
    if (_query.statusIds.isNotEmpty) count++;
    if (_query.statusTypes.isNotEmpty) count++;
    if (_query.categoryIds.isNotEmpty) count++;
    if (_query.tagIds.isNotEmpty) count++;
    if (_query.priorities.isNotEmpty) count++;
    if (_query.overdueOnly) count++;
    if (_query.includeCompleted) count++;
    return count;
  }
}

class _Toolbar extends StatelessWidget {
  final ViewMode mode;
  final ValueChanged<ViewMode> onModeChanged;
  final TextEditingController search;
  final FocusNode searchFocus;
  final ValueChanged<String> onSearchChanged;
  final bool filtersOpen;
  final int filterCount;
  final VoidCallback onToggleFilters;
  final VoidCallback onNewTask;
  final VoidCallback onOpenSettings;

  const _Toolbar({
    required this.mode,
    required this.onModeChanged,
    required this.search,
    required this.searchFocus,
    required this.onSearchChanged,
    required this.filtersOpen,
    required this.filterCount,
    required this.onToggleFilters,
    required this.onNewTask,
    required this.onOpenSettings,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 10, 12, 10),
      child: Row(
        children: [
          SegmentedButton<ViewMode>(
            segments: const [
              ButtonSegment(
                value: ViewMode.list,
                icon: Icon(Icons.view_list_outlined, size: 18),
                label: Text('List'),
              ),
              ButtonSegment(
                value: ViewMode.board,
                icon: Icon(Icons.view_kanban_outlined, size: 18),
                label: Text('Board'),
              ),
            ],
            selected: {mode},
            showSelectedIcon: false,
            onSelectionChanged: (s) => onModeChanged(s.first),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: TextField(
              controller: search,
              focusNode: searchFocus,
              onChanged: onSearchChanged,
              decoration: InputDecoration(
                hintText: 'Search summaries, descriptions, notes and steps',
                prefixIcon: const Icon(Icons.search, size: 18),
                suffixIcon: search.text.isEmpty
                    ? null
                    : IconButton(
                        icon: const Icon(Icons.close, size: 16),
                        onPressed: () {
                          search.clear();
                          onSearchChanged('');
                        },
                      ),
              ),
            ),
          ),
          const SizedBox(width: 8),
          Badge(
            isLabelVisible: filterCount > 0,
            label: Text('$filterCount'),
            child: IconButton(
              tooltip: 'Filters',
              isSelected: filtersOpen,
              icon: const Icon(Icons.filter_alt_outlined),
              selectedIcon: const Icon(Icons.filter_alt),
              onPressed: onToggleFilters,
            ),
          ),
          IconButton(
            tooltip: 'Settings',
            icon: const Icon(Icons.settings_outlined),
            onPressed: onOpenSettings,
          ),
          const SizedBox(width: 8),
          FilledButton.icon(
            onPressed: onNewTask,
            icon: const Icon(Icons.add, size: 18),
            label: const Text('New task'),
          ),
        ],
      ),
    );
  }
}

class _ConflictBanner extends StatelessWidget {
  final int count;

  const _ConflictBanner({required this.count});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Material(
      color: scheme.tertiaryContainer,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
        child: Row(
          children: [
            Icon(Icons.sync_problem, size: 18, color: scheme.onTertiaryContainer),
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                count == 1
                    ? 'Your sync client left 1 conflicting copy of a task. '
                        'Two machines edited it at once.'
                    : 'Your sync client left $count conflicting copies of '
                        'tasks. Two machines edited them at once.',
                style: TextStyle(color: scheme.onTertiaryContainer),
              ),
            ),
            TextButton(
              onPressed: () => Navigator.of(context).push(
                MaterialPageRoute<void>(
                  builder: (_) => ChangeNotifierProvider.value(
                    value: context.read<WorkspaceController>(),
                    child: const ConflictsScreen(),
                  ),
                ),
              ),
              child: const Text('Review'),
            ),
          ],
        ),
      ),
    );
  }
}

class _FailureBanner extends StatelessWidget {
  final int count;

  const _FailureBanner({required this.count});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Material(
      color: scheme.errorContainer,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
        child: Row(
          children: [
            Icon(Icons.warning_amber, size: 18, color: scheme.onErrorContainer),
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                '$count task ${count == 1 ? 'file' : 'files'} could not be '
                'read. They are still on disk and have not been touched.',
                style: TextStyle(color: scheme.onErrorContainer),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _StatusBar extends StatelessWidget {
  final int total;
  final int showing;
  final TextStyle? style;

  const _StatusBar({
    required this.total,
    required this.showing,
    required this.style,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        border: Border(
          top: BorderSide(color: Theme.of(context).colorScheme.outlineVariant),
        ),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
      child: Row(
        children: [
          Text(
            showing == total
                ? '$total ${total == 1 ? 'task' : 'tasks'}'
                : 'Showing $showing of $total tasks',
            style: style,
          ),
        ],
      ),
    );
  }
}

/// Lists sync-conflict files so they can be resolved rather than discovered
/// months later.
class ConflictsScreen extends StatelessWidget {
  const ConflictsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final controller = context.watch<WorkspaceController>();
    final conflicts = controller.conflicts;

    return Scaffold(
      appBar: AppBar(title: const Text('Sync conflicts')),
      body: conflicts.isEmpty
          ? const Center(child: Text('No conflicting copies. Nothing to do.'))
          : ListView.separated(
              itemCount: conflicts.length + 1,
              separatorBuilder: (_, _) => const Divider(height: 1),
              itemBuilder: (context, index) {
                if (index == 0) {
                  return Padding(
                    padding: const EdgeInsets.all(16),
                    child: Text(
                      'These files were left by OneDrive, Dropbox or iCloud '
                      'when the same task was edited on two machines. '
                      'might-do has not touched them. Open one alongside the '
                      'task it copies to see which version you want, then '
                      'delete the copy from your file manager.',
                      style: Theme.of(context).textTheme.bodyMedium,
                    ),
                  );
                }

                final conflict = conflicts[index - 1];
                final task = conflict.taskId == null
                    ? null
                    : controller.taskById(conflict.taskId!);

                return ListTile(
                  leading: const Icon(Icons.sync_problem),
                  title: Text(conflict.fileName),
                  subtitle: Text(
                    task == null
                        ? 'Modified ${conflict.modifiedAt}'
                        : 'Copy of "${task.summary}" — modified '
                            '${conflict.modifiedAt}',
                  ),
                );
              },
            ),
    );
  }
}
