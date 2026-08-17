import 'package:flutter/material.dart';

import '../app/task_query.dart';
import '../domain/status.dart';
import '../domain/task.dart';
import '../domain/workspace_config.dart';
import 'theme.dart';

/// The filter and sort strip beneath the toolbar.
class FilterBar extends StatelessWidget {
  final TaskQuery query;
  final WorkspaceConfig config;
  final ValueChanged<TaskQuery> onChanged;

  const FilterBar({
    super.key,
    required this.query,
    required this.config,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Container(
      color: theme.colorScheme.surfaceContainerLow,
      padding: const EdgeInsets.fromLTRB(12, 10, 12, 10),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Wrap(
            spacing: 16,
            runSpacing: 10,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              _Group(
                label: 'Stage',
                child: Wrap(
                  spacing: 6,
                  children: [
                    for (final type in StatusType.values)
                      FilterChip(
                        label: Text(type.label),
                        selected: query.statusTypes.contains(type),
                        visualDensity: VisualDensity.compact,
                        onSelected: (selected) => onChanged(
                          query.copyWith(
                            statusTypes: _toggle(query.statusTypes, type, selected),
                          ),
                        ),
                      ),
                  ],
                ),
              ),
              _Group(
                label: 'Priority',
                child: Wrap(
                  spacing: 6,
                  children: [
                    for (final priority in Priority.values)
                      FilterChip(
                        label: Text(priority.label),
                        selected: query.priorities.contains(priority),
                        visualDensity: VisualDensity.compact,
                        avatar: Icon(
                          Icons.flag,
                          size: 13,
                          color: priorityColor(priority, theme.colorScheme),
                        ),
                        onSelected: (selected) => onChanged(
                          query.copyWith(
                            priorities:
                                _toggle(query.priorities, priority, selected),
                          ),
                        ),
                      ),
                  ],
                ),
              ),
              _Group(
                label: 'Show',
                child: Wrap(
                  spacing: 6,
                  children: [
                    FilterChip(
                      label: const Text('Overdue only'),
                      selected: query.overdueOnly,
                      visualDensity: VisualDensity.compact,
                      onSelected: (selected) =>
                          onChanged(query.copyWith(overdueOnly: selected)),
                    ),
                    FilterChip(
                      label: const Text('Include completed'),
                      selected: query.includeCompleted,
                      visualDensity: VisualDensity.compact,
                      onSelected: (selected) =>
                          onChanged(query.copyWith(includeCompleted: selected)),
                    ),
                  ],
                ),
              ),
              _Group(
                label: 'Sort',
                child: DropdownButton<TaskSort>(
                  value: query.sort,
                  isDense: true,
                  underline: const SizedBox.shrink(),
                  items: [
                    for (final sort in TaskSort.values)
                      DropdownMenuItem(value: sort, child: Text(sort.label)),
                  ],
                  onChanged: (value) {
                    if (value != null) onChanged(query.copyWith(sort: value));
                  },
                ),
              ),
            ],
          ),
          if (config.categories.isNotEmpty || config.tags.isNotEmpty) ...[
            const SizedBox(height: 10),
            Wrap(
              spacing: 16,
              runSpacing: 10,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                if (config.categories.isNotEmpty)
                  _Group(
                    label: 'Category',
                    child: Wrap(
                      spacing: 6,
                      runSpacing: 6,
                      children: [
                        for (final category in config.categories)
                          FilterChip(
                            label: Text(category.name),
                            selected: query.categoryIds.contains(category.id),
                            visualDensity: VisualDensity.compact,
                            avatar: Container(
                              width: 10,
                              height: 10,
                              decoration: BoxDecoration(
                                color: Color(category.color),
                                shape: BoxShape.circle,
                              ),
                            ),
                            onSelected: (selected) => onChanged(
                              query.copyWith(
                                categoryIds: _toggle(
                                  query.categoryIds,
                                  category.id,
                                  selected,
                                ),
                              ),
                            ),
                          ),
                      ],
                    ),
                  ),
                if (config.tags.isNotEmpty)
                  _Group(
                    label: 'Tags',
                    child: Wrap(
                      spacing: 6,
                      runSpacing: 6,
                      children: [
                        for (final tag in config.tags)
                          FilterChip(
                            label: Text('#${tag.name}'),
                            selected: query.tagIds.contains(tag.id),
                            visualDensity: VisualDensity.compact,
                            onSelected: (selected) => onChanged(
                              query.copyWith(
                                tagIds:
                                    _toggle(query.tagIds, tag.id, selected),
                              ),
                            ),
                          ),
                      ],
                    ),
                  ),
              ],
            ),
          ],
          if (query.isFiltered) ...[
            const SizedBox(height: 6),
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton.icon(
                icon: const Icon(Icons.clear_all, size: 16),
                label: const Text('Clear filters'),
                onPressed: () => onChanged(TaskQuery(sort: query.sort)),
              ),
            ),
          ],
        ],
      ),
    );
  }

  static Set<T> _toggle<T>(Set<T> current, T value, bool selected) {
    final next = {...current};
    if (selected) {
      next.add(value);
    } else {
      next.remove(value);
    }
    return next;
  }
}

class _Group extends StatelessWidget {
  final String label;
  final Widget child;

  const _Group({required this.label, required this.child});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          label,
          style: theme.textTheme.labelSmall?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(width: 8),
        child,
      ],
    );
  }
}
