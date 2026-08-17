import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'app/app_settings.dart';
import 'app/reminder_service.dart';
import 'app/workspace_controller.dart';
import 'storage/task_store.dart';
import 'storage/workspace.dart';
import 'ui/home_screen.dart';
import 'ui/theme.dart';
import 'ui/workspace_picker.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final settings = await AppSettings.load();
  runApp(MightDoApp(settings: settings));
}

class MightDoApp extends StatefulWidget {
  final AppSettings settings;

  const MightDoApp({super.key, required this.settings});

  @override
  State<MightDoApp> createState() => _MightDoAppState();
}

class _MightDoAppState extends State<MightDoApp> {
  WorkspaceController? _controller;
  ReminderService? _reminders;
  String? _openPath;

  @override
  void initState() {
    super.initState();
    final remembered = widget.settings.workspacePath;
    if (remembered != null) _openWorkspace(remembered);
  }

  Future<void> _openWorkspace(String path) async {
    final controller = WorkspaceController(TaskStore(Workspace.at(path)));
    await controller.open();
    await widget.settings.setWorkspacePath(path);

    // Reminders fire only while the app is open. Anything that fell due while
    // it was closed is caught by the in-app overdue banner instead.
    final reminders = ReminderService(controller);
    unawaited(reminders.start());

    if (!mounted) {
      reminders.dispose();
      controller.dispose();
      return;
    }
    setState(() {
      _reminders?.dispose();
      _controller?.dispose();
      _reminders = reminders;
      _controller = controller;
      _openPath = path;
    });
  }

  Future<void> _closeWorkspace() async {
    await widget.settings.forgetWorkspace();
    if (!mounted) return;
    setState(() {
      _reminders?.dispose();
      _controller?.dispose();
      _reminders = null;
      _controller = null;
      _openPath = null;
    });
  }

  @override
  void dispose() {
    _reminders?.dispose();
    _controller?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final controller = _controller;

    return MaterialApp(
      title: 'might-do',
      debugShowCheckedModeBanner: false,
      theme: buildTheme(Brightness.light),
      darkTheme: buildTheme(Brightness.dark),
      home: controller == null
          ? WorkspacePicker(
              rememberedPath: widget.settings.rememberedWorkspacePath,
              onChosen: _openWorkspace,
            )
          : MultiProvider(
              key: ValueKey(_openPath),
              providers: [
                ChangeNotifierProvider.value(value: controller),
                Provider<AppSettings>.value(value: widget.settings),
              ],
              child: HomeScreen(onCloseWorkspace: _closeWorkspace),
            ),
    );
  }
}
