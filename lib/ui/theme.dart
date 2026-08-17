import 'package:flutter/material.dart';

import '../domain/task.dart';

const Color _seed = Color(0xFF4C6EF5);

ThemeData buildTheme(Brightness brightness) {
  final scheme = ColorScheme.fromSeed(
    seedColor: _seed,
    brightness: brightness,
  );

  return ThemeData(
    colorScheme: scheme,
    useMaterial3: true,
    visualDensity: VisualDensity.compact,
    scaffoldBackgroundColor: scheme.surface,
    inputDecorationTheme: const InputDecorationTheme(
      border: OutlineInputBorder(),
      isDense: true,
    ),
    dividerTheme: DividerThemeData(
      color: scheme.outlineVariant,
      space: 1,
      thickness: 1,
    ),
    cardTheme: CardThemeData(
      elevation: 0,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(8),
        side: BorderSide(color: scheme.outlineVariant),
      ),
    ),
  );
}

/// Colour for a priority chip. Deliberately not the theme's error colour for
/// Critical — an urgent task isn't an error state.
Color priorityColor(Priority priority, ColorScheme scheme) {
  switch (priority) {
    case Priority.low:
      return scheme.outline;
    case Priority.medium:
      return scheme.primary;
    case Priority.high:
      return const Color(0xFFE8590C);
    case Priority.critical:
      return const Color(0xFFC92A2A);
  }
}

/// The palette offered when creating a category.
const List<int> categoryPalette = [
  0xFF4C6EF5,
  0xFF15AABF,
  0xFF12B886,
  0xFF40C057,
  0xFF82C91E,
  0xFFFAB005,
  0xFFFD7E14,
  0xFFE8590C,
  0xFFF03E3E,
  0xFFE64980,
  0xFFBE4BDB,
  0xFF7950F2,
];
