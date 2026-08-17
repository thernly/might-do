import 'dart:io';

import 'package:shared_preferences/shared_preferences.dart';

/// Preferences that belong to this machine rather than to the workspace.
///
/// The workspace path lives here and not in the workspace itself — the folder
/// sits at a different path on each machine, so storing it alongside the tasks
/// would mean syncing a value that is wrong everywhere but where it was
/// written.
class AppSettings {
  static const _workspacePathKey = 'workspace.path';
  static const _viewModeKey = 'ui.viewMode';

  final SharedPreferences _prefs;

  AppSettings(this._prefs);

  static Future<AppSettings> load() async =>
      AppSettings(await SharedPreferences.getInstance());

  /// The chosen workspace folder, or null if the user hasn't picked one or the
  /// folder has since gone (an unmounted drive, a moved OneDrive folder).
  String? get workspacePath {
    final path = _prefs.getString(_workspacePathKey);
    if (path == null) return null;
    return Directory(path).existsSync() ? path : null;
  }

  /// The stored path even if it no longer resolves, so the app can say
  /// "couldn't find your workspace at X" rather than silently starting over.
  String? get rememberedWorkspacePath => _prefs.getString(_workspacePathKey);

  Future<void> setWorkspacePath(String path) =>
      _prefs.setString(_workspacePathKey, path);

  Future<void> forgetWorkspace() => _prefs.remove(_workspacePathKey);

  String get viewMode => _prefs.getString(_viewModeKey) ?? 'list';

  Future<void> setViewMode(String mode) =>
      _prefs.setString(_viewModeKey, mode);
}
