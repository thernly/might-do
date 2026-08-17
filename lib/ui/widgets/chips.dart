import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../domain/calendar_date.dart';
import '../../domain/status.dart';
import '../../domain/task.dart';
import '../theme.dart';

/// A small flat label. Used for priority, category, tags and dates so they read
/// as one family rather than four different controls.
class MiniChip extends StatelessWidget {
  final String label;
  final Color color;
  final IconData? icon;
  final bool filled;

  const MiniChip({
    super.key,
    required this.label,
    required this.color,
    this.icon,
    this.filled = false,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
      decoration: BoxDecoration(
        color: filled ? color.withValues(alpha: 0.14) : null,
        border: Border.all(color: color.withValues(alpha: filled ? 0.0 : 0.5)),
        borderRadius: BorderRadius.circular(4),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, size: 12, color: color),
            const SizedBox(width: 4),
          ],
          Text(
            label,
            style: TextStyle(
              fontSize: 11,
              height: 1.3,
              color: color,
              fontWeight: FontWeight.w500,
            ),
          ),
        ],
      ),
    );
  }
}

class PriorityChip extends StatelessWidget {
  final Priority priority;

  const PriorityChip({super.key, required this.priority});

  @override
  Widget build(BuildContext context) => MiniChip(
        label: priority.label,
        color: priorityColor(priority, Theme.of(context).colorScheme),
        filled: priority == Priority.high || priority == Priority.critical,
      );
}

class CategoryChip extends StatelessWidget {
  final Category category;

  const CategoryChip({super.key, required this.category});

  @override
  Widget build(BuildContext context) =>
      MiniChip(label: category.name, color: Color(category.color), filled: true);
}

class TagChip extends StatelessWidget {
  final Tag tag;
  final VoidCallback? onDeleted;

  const TagChip({super.key, required this.tag, this.onDeleted});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    if (onDeleted == null) {
      return MiniChip(label: '#${tag.name}', color: scheme.onSurfaceVariant);
    }
    return InputChip(
      label: Text('#${tag.name}'),
      labelStyle: const TextStyle(fontSize: 11),
      onDeleted: onDeleted,
      visualDensity: VisualDensity.compact,
      materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
    );
  }
}

/// Renders a due date in human terms — "Today", "Tomorrow", "3 days ago" —
/// because a bare date makes you do the arithmetic yourself.
class DueDateChip extends StatelessWidget {
  final CalendarDate date;
  final bool isComplete;

  const DueDateChip({
    super.key,
    required this.date,
    required this.isComplete,
  });

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final days = CalendarDate.today().daysUntil(date);
    final overdue = days < 0 && !isComplete;

    final color = isComplete
        ? scheme.onSurfaceVariant
        : overdue
            ? const Color(0xFFC92A2A)
            : days <= 1
                ? const Color(0xFFE8590C)
                : scheme.onSurfaceVariant;

    return MiniChip(
      label: describeDueDate(date),
      color: color,
      icon: Icons.event_outlined,
      filled: overdue,
    );
  }
}

String describeDueDate(CalendarDate date) {
  final days = CalendarDate.today().daysUntil(date);
  if (days == 0) return 'Today';
  if (days == 1) return 'Tomorrow';
  if (days == -1) return 'Yesterday';
  if (days < 0 && days >= -13) return '${-days} days ago';
  if (days > 0 && days <= 13) return 'In $days days';

  final dateTime = date.toLocalDateTime();
  final sameYear = dateTime.year == DateTime.now().year;
  return DateFormat(sameYear ? 'd MMM' : 'd MMM yyyy').format(dateTime);
}

/// Formats a duration held as whole minutes, e.g. `2h 30m`.
String formatMinutes(int minutes) {
  if (minutes < 60) return '${minutes}m';
  final hours = minutes ~/ 60;
  final rest = minutes % 60;
  return rest == 0 ? '${hours}h' : '${hours}h ${rest}m';
}

/// Parses `90`, `90m`, `2h`, `2h30m`, `2:30` into whole minutes.
int? parseMinutes(String input) {
  final text = input.trim().toLowerCase();
  if (text.isEmpty) return null;

  final clock = RegExp(r'^(\d+):([0-5]\d)$').firstMatch(text);
  if (clock != null) {
    return int.parse(clock.group(1)!) * 60 + int.parse(clock.group(2)!);
  }

  final composite = RegExp(r'^(?:(\d+)\s*h)?\s*(?:(\d+)\s*m)?$').firstMatch(text);
  if (composite != null &&
      (composite.group(1) != null || composite.group(2) != null)) {
    final hours = int.tryParse(composite.group(1) ?? '0') ?? 0;
    final minutes = int.tryParse(composite.group(2) ?? '0') ?? 0;
    return hours * 60 + minutes;
  }

  return int.tryParse(text);
}

String formatInstant(DateTime utc) =>
    DateFormat('d MMM yyyy, HH:mm').format(utc.toLocal());
