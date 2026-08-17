/// The fixed classification every status belongs to.
///
/// This set is closed and not user-editable — see
/// `docs/adr/0002-statuses-are-user-data-typed-by-a-closed-set.md`. Users
/// invent and rename statuses freely; the application reasons about types.
///
/// The domain term for [finalType] is simply "Final". `final` is a reserved
/// word in Dart, so the identifier carries a suffix the domain does not.
enum StatusType {
  /// Work not yet begun. Covers `Backlog`, `Ready`, `Not Started`.
  initial('initial', 'Initial'),

  /// Work under way. Covers `In Progress`, `Blocked`, `In Review` — a blocked
  /// task is still active work.
  active('active', 'Active'),

  /// Work concluded, whether or not it was done. Covers `Done`, `Abandoned`.
  /// Entering any status of this type stamps the task's completion date.
  finalType('final', 'Final');

  const StatusType(this.wire, this.label);

  /// Value written to JSON. Stable; never derive it from [name].
  final String wire;

  /// Human-readable name shown in the UI.
  final String label;

  static StatusType fromWire(String wire) => values.firstWhere(
        (type) => type.wire == wire,
        orElse: () => throw FormatException('Unknown status type', wire),
      );
}

/// A stage a task can be in, named and ordered by the user, and rendered as a
/// column of the Kanban view.
class Status {
  final String id;
  final String name;
  final StatusType type;

  /// Position among all statuses. Also the left-to-right column order.
  final int order;

  /// Keeps this status off the Kanban view while leaving it an ordinary status
  /// everywhere else. Exists so a `Backlog` holding hundreds of cards doesn't
  /// swamp columns holding five.
  final bool hiddenFromBoard;

  const Status({
    required this.id,
    required this.name,
    required this.type,
    required this.order,
    this.hiddenFromBoard = false,
  });

  Status copyWith({
    String? name,
    StatusType? type,
    int? order,
    bool? hiddenFromBoard,
  }) =>
      Status(
        id: id,
        name: name ?? this.name,
        type: type ?? this.type,
        order: order ?? this.order,
        hiddenFromBoard: hiddenFromBoard ?? this.hiddenFromBoard,
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'name': name,
        'type': type.wire,
        'order': order,
        'hiddenFromBoard': hiddenFromBoard,
      };

  static Status fromJson(Map<String, dynamic> json) => Status(
        id: json['id'] as String,
        name: json['name'] as String,
        type: StatusType.fromWire(json['type'] as String),
        order: json['order'] as int,
        hiddenFromBoard: json['hiddenFromBoard'] as bool? ?? false,
      );

  @override
  String toString() => 'Status($id, $name, ${type.wire})';
}

/// A user-defined grouping answering "what area of my life is this?".
/// A task has at most one.
class Category {
  final String id;
  final String name;

  /// ARGB colour used for the chip in list and board views.
  final int color;

  const Category({required this.id, required this.name, required this.color});

  Category copyWith({String? name, int? color}) => Category(
        id: id,
        name: name ?? this.name,
        color: color ?? this.color,
      );

  Map<String, dynamic> toJson() => {'id': id, 'name': name, 'color': color};

  static Category fromJson(Map<String, dynamic> json) => Category(
        id: json['id'] as String,
        name: json['name'] as String,
        color: json['color'] as int,
      );
}

/// A lightweight cross-cutting label. A task may carry several, up to
/// [Task.maxTags].
class Tag {
  final String id;
  final String name;

  const Tag({required this.id, required this.name});

  Tag copyWith({String? name}) => Tag(id: id, name: name ?? this.name);

  Map<String, dynamic> toJson() => {'id': id, 'name': name};

  static Tag fromJson(Map<String, dynamic> json) => Tag(
        id: json['id'] as String,
        name: json['name'] as String,
      );
}
