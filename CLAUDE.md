# SensorPanel

A WPF app (.NET 10, `net10.0-windows`, nullable and implicit usings on) that draws live hardware sensor panels on an
AX206 USB screen, an HDMI/DisplayPort monitor or a PNG file, with an editor to design them. README.md is the user
guide; read it before changing behaviour.

## Layout

- `SensorPanel\`: the app.
  - `Engine\PanelEngine.cs`: sensor, render and display threads, pages, rotation, the pop-out mirror.
  - `Rendering\PanelRenderer.cs`: draws a layout to a bitmap.
  - `Model\`: the panel (`PanelModel.cs`), saving and loading (`PanelStore.cs`), import (`PanelImporter.cs`).
  - `Sensors\`: LibreHardwareMonitor (`SensorHub`), framerate via ETW (`FrameCounter`, `FullScreen`), top processes
    (`ProcessMonitor`), optional RTSS framerate, ID matching for imported panels (`SensorMatcher`).
  - `Display\`: the built-in HDMI driver, the plugin loader and the display worker thread.
  - `Services\`: the Start with Windows task (`StartupTask`) and in-app updates (`UpdateService`).
  - `UI\`: the properties editor and the pop-out display window.
- `SensorPanel.Plugins\`: the display plugin contract (`IDisplayDriver`, `IDisplay`, `DisplayFrame`).
- `Plugins\Ax206`, `Plugins\ImageFile`: display plugins. `Ax206Tool\`: an AX206 command-line test tool.
- `Installer\`: WiX 5 per-machine MSI. `tools\`: release, test and screenshot scripts. `docs\`: screenshots.

## Building

- `SensorPanel\build.ps1` builds the app and plugins into `App\`. Exit a copy running from `App\` first (signal the
  named event `SensorPanel.Exit`; that works without administrator rights).
- Test builds go to their own output folder so they don't clash with a running copy:
  `dotnet build SensorPanel\SensorPanel.csproj -c Release "-p:BaseOutputPath=obj\<name>\"`.
- `Plugins\ImageFile\build.ps1` rebuilds only that plugin into `App\plugins\ImageFile` (restart SensorPanel after).
- `Installer\build-installer.ps1` builds `Installer\out\SensorPanel-<version>-x64.msi` (self-contained, with plugins).
- After building, `dotnet build-server shutdown` releases locks on the folder.
- The plugin contract's `AssemblyVersion` is fixed at 1.0.0.0. Don't let a global `-p:Version` change it, or existing
  plugins stop loading.

## Rules

- Don't name commercial sensor-panel products in code, comments, strings, paths or docs. The importer is
  `PanelImporter`; imported sensor IDs (e.g. `TCPU`) use the `match:` prefix and are resolved by `SensorMatcher`.
- Don't decompile or copy from proprietary tools (other sensor panels, RivaTuner). Use documented APIs and openly
  licensed code, and credit it in the README.
- Framerate counts only full-screen apps (exclusive or borderless), from ETW. Nothing is injected into games.
- Display plugins live only in the `plugins` folder next to the exe (`App\plugins` for the `App\` build).
- Keep README.md current in the same change: UI or behaviour changes update it, and visible UI changes retake the
  screenshots with `tools\capture-screenshots.ps1`. Plain, specific English; wrap Markdown at 120 columns. No details
  of anyone's own PC in the text (the screenshots are the exception).
- Match the surrounding code: comments explain why, in full sentences; UI text says what happens.

## How it works (things that aren't obvious)

- SensorPanel runs elevated (hardware sensors need it) and is single-instance: a second start signals
  `SensorPanel.ShowWindow` and exits. `--no-elevate`, `--minimized`, `--restarted`, `--exit`, `--enable-startup` and
  `--disable-startup` are handled in `App.xaml.cs`.
- Rotation: the panel is drawn and sent in `DesignRotation`. `Rotation` only turns the preview and pop-out
  (`PreviewTurn`), so changing it never moves items or changes what the screen shows.
- Pages: `PanelItem.Page` 0 means every page. The engine rotates the displayed page every `PageSeconds`.
- The AX206 takes about 450 ms for a full frame, so only changed bands are sent.
- Start with Windows is the scheduled task "SensorPanel" (`Services\StartupTask.cs`). It has a sign-in trigger and an
  event trigger that starts SensorPanel again 15 s after an "Application Error" for it: an NVML call inside
  LibreHardwareMonitor can crash with an access violation (not catchable in .NET) while the graphics driver is
  updated. The task starts whichever copy turned it on. A `--minimized` start (the task's) after 3 crashes of the same
  exe path in 5 minutes shows a "stopped" tray notice instead of starting (`App.ShowStoppedNotice`).
- LibreHardwareMonitor needs PawnIO installed properly (it checks PawnIO's uninstall entry) for CPU, motherboard and
  memory sensors.
- WPF renders in software (`RenderMode.SoftwareOnly`); the GPU path drew an empty window on some PCs.

## Testing

- The developer's own panel and settings in `%APPDATA%\SensorPanel` (`panel.json`, `settings.json`, `images\`) are
  real data. Back them up before a test and compare afterwards; never add them to git.
- Close the running copy first (single instance), run the test copy (`--no-elevate` unless the test needs sensors or
  the startup task), then start the normal copy again.
- To test drawing without the real screen, use the Picture file (PNG) display type writing to a scratch file.
- Tests that touch the startup task: export it first (`Export-ScheduledTask -TaskName SensorPanel`) and put it back
  with `schtasks /Create /TN SensorPanel /XML <file> /F`. `tools\test-startup-task.ps1` covers the task's update,
  crash restart and crash limit, and does this itself.
- Non-elevated tools can't automate or screenshot the elevated window (UIPI); capture from an elevated script with
  PrintWindow.
- Never click **Download and install** in a test: it installs into Program Files.
- `tools\test-update-check.ps1` checks the update check against the real releases without installing anything.

## Releasing

1. Update README.md (and CLAUDE.md if needed) for the change, bump `<Version>` in `SensorPanel\SensorPanel.csproj`,
   commit and push. The script refuses to publish with uncommitted changes or an unpushed commit, and tags `HEAD`.
2. `powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -Notes <notes.md>` builds the MSI and publishes
   GitHub release `v<version>` with it attached (GitHub CLI if installed, otherwise git's stored sign-in). Pass
   `-SkipBuild` to upload the MSI already in `Installer\out\` (e.g. one you installed and tested). The About tab's
   updater needs the `vX.Y.Z` tag and the `.msi` asset, and checks it against GitHub's SHA-256 digest.
3. A release is public: publish only when the maintainer asks.
