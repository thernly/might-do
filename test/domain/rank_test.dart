import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:might_do/domain/rank.dart';

void main() {
  group('rankBetween', () {
    test('produces a rank for an empty column', () {
      final rank = rankBetween('', '');
      expect(rank, isNotEmpty);
    });

    test('appends after an existing rank', () {
      final first = rankBetween('', '');
      final second = rankBetween(first, '');
      expect(first.compareTo(second), lessThan(0));
    });

    test('inserts before an existing rank', () {
      final first = rankBetween('', '');
      final earlier = rankBetween('', first);
      expect(earlier.compareTo(first), lessThan(0));
    });

    test('inserts strictly between two adjacent ranks', () {
      var low = rankBetween('', '');
      var high = rankBetween(low, '');

      // Repeatedly halving the same gap is the case that exhausts naive
      // implementations.
      for (var i = 0; i < 200; i++) {
        final middle = rankBetween(low, high);
        expect(low.compareTo(middle), lessThan(0),
            reason: 'iteration $i: $low !< $middle');
        expect(middle.compareTo(high), lessThan(0),
            reason: 'iteration $i: $middle !< $high');
        low = middle;
      }
    });

    test('survives repeatedly inserting at the very top', () {
      var top = rankBetween('', '');
      for (var i = 0; i < 200; i++) {
        final above = rankBetween('', top);
        expect(above.compareTo(top), lessThan(0),
            reason: 'iteration $i: $above !< $top');
        top = above;
      }
    });

    test('never emits a trailing zero', () {
      var low = rankBetween('', '');
      var high = rankBetween(low, '');
      for (var i = 0; i < 100; i++) {
        final middle = rankBetween(low, high);
        expect(middle.endsWith('0'), isFalse, reason: middle);
        high = middle;
      }
    });

    test('rejects bounds in the wrong order', () {
      final low = rankBetween('', '');
      final high = rankBetween(low, '');
      expect(() => rankBetween(high, low), throwsArgumentError);
      expect(() => rankBetween(low, low), throwsArgumentError);
    });

    test('keeps a column ordered through random reordering', () {
      final random = Random(1234);
      final ranks = initialRanks(12);
      expect(ranks, orderedEquals([...ranks]..sort()));

      // Simulate 500 drag-and-drops: pull a card out and drop it somewhere else.
      for (var i = 0; i < 500; i++) {
        final from = random.nextInt(ranks.length);
        ranks.removeAt(from);
        final to = random.nextInt(ranks.length + 1);
        final before = to == 0 ? '' : ranks[to - 1];
        final after = to == ranks.length ? '' : ranks[to];
        ranks.insert(to, rankBetween(before, after));

        final sorted = [...ranks]..sort();
        expect(ranks, orderedEquals(sorted),
            reason: 'order broke on iteration $i');
      }
    });
  });

  group('initialRanks', () {
    test('returns the requested count in ascending order', () {
      final ranks = initialRanks(5);
      expect(ranks, hasLength(5));
      expect(ranks, orderedEquals([...ranks]..sort()));
      expect(ranks.toSet(), hasLength(5));
    });

    test('returns nothing for zero items', () {
      expect(initialRanks(0), isEmpty);
    });
  });
}
