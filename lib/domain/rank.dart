/// Fractional index ranks for manual board ordering.
///
/// A rank is a string that sorts lexicographically. To drop a card between two
/// others you generate a rank between their two ranks — which rewrites exactly
/// one task file, rather than renumbering every card in the column. That
/// matters here because task files sync individually, so a reorder that touched
/// every file would be the worst possible conflict shape.
///
/// Ranks are base-36 (`0-9a-z`), which sorts correctly under plain string
/// comparison. No rank ever ends in `0`; that invariant is what guarantees
/// there is always room to insert before any existing rank.
library;

const String _digits = '0123456789abcdefghijklmnopqrstuvwxyz';
const int _base = 36;

/// Returns a rank that sorts strictly between [before] and [after].
///
/// Pass an empty string for [before] to insert at the very top, and an empty
/// string for [after] to append at the very bottom. `rankBetween('', '')`
/// produces the first rank in an empty column.
String rankBetween(String before, String after) {
  if (before.isNotEmpty && !_isValidRank(before)) {
    throw ArgumentError.value(before, 'before', 'Not a valid rank');
  }
  if (after.isNotEmpty && !_isValidRank(after)) {
    throw ArgumentError.value(after, 'after', 'Not a valid rank');
  }
  if (before.isNotEmpty && after.isNotEmpty && before.compareTo(after) >= 0) {
    throw ArgumentError('before ($before) must sort before after ($after)');
  }

  final buffer = StringBuffer();
  var boundedAbove = after.isNotEmpty;
  var i = 0;

  while (true) {
    // -1 stands for "below the smallest digit"; _base for "above the largest".
    final low = i < before.length ? _digits.indexOf(before[i]) : -1;
    final high = boundedAbove && i < after.length
        ? _digits.indexOf(after[i])
        : _base;

    if (low + 1 < high) {
      final mid = (low + 1 + high) ~/ 2;
      if (mid == 0) {
        // Emitting a terminal '0' would break the no-trailing-zero invariant,
        // so descend a level instead. '0' is strictly below `high` here, so
        // `after` no longer constrains us.
        buffer.write(_digits[0]);
        boundedAbove = false;
        i++;
        continue;
      }
      buffer.write(_digits[mid]);
      return buffer.toString();
    }

    // No gap at this position: copy the lower bound's digit and go deeper.
    buffer.write(low >= 0 ? _digits[low] : _digits[0]);
    if (low >= 0 && low < high) boundedAbove = false;
    i++;
  }
}

/// Ranks for [count] items in order, for seeding a fresh column.
List<String> initialRanks(int count) {
  final ranks = <String>[];
  var previous = '';
  for (var i = 0; i < count; i++) {
    previous = rankBetween(previous, '');
    ranks.add(previous);
  }
  return ranks;
}

bool _isValidRank(String rank) {
  if (rank.isEmpty) return false;
  if (rank.endsWith(_digits[0])) return false;
  for (final char in rank.split('')) {
    if (!_digits.contains(char)) return false;
  }
  return true;
}
