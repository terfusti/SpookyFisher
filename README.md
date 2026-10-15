# SpookyFisher

A Windows Terraria fishing tool with a native CLR bridge and managed Harmony features.
The current host supports .NET Framework CLR v4; Mono and CoreCLR are rejected.

## Build and run

Install Visual Studio's C++ tools (`v145`), Windows 10 SDK, .NET Framework 4.7.2
targeting pack, and the XNA assemblies used by your Terraria installation.
Use a Visual Studio Developer Command Prompt at the repository root:

```powershell
Copy-Item Local.props.example Local.props
# Edit TerrariaDirectory in Local.props to match your installation.
nuget restore SpookyFisher.sln
msbuild SpookyFisher.sln /m /p:Configuration=Debug /p:Platform=x86
```

Use `Configuration=Release` for optimized builds. All three outputs share
`bin/Win32/<Configuration>/`. `Local.props` is ignored by Git; alternatively pass
`/p:TerrariaDirectory="D:\path\to\Terraria"`. `XnaAssemblyRoot` defaults to the
Windows .NET Framework `GAC_32` directory.

The installed Terraria/XNA references are x86. The solution also maps x64 outputs
to `bin/x64/<Configuration>/`, but running them requires compatible x64 game and
framework references. Changing the build platform does not port Terraria's runtime.

Start Terraria, reach its main menu, then run:

```powershell
.\bin\Win32\Debug\SpookyInjector.exe
```

For sharp rendering on a high-DPI display, close Terraria and launch it with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Start-Terraria.ps1
```

This uses `TerrariaDirectory` from `Local.props`, starts the game with application
DPI scaling, and injects the Debug build. Pass `-Configuration Release` for Release.
The setting applies only to that launched process. This avoids Windows stretching
a low-resolution game frame; the menu scales its fonts and spacing for display DPI.

The injector loads `SpookyFisher.dll` beside itself, or accepts an explicit DLL path.
Keep `SpookyFisher.Managed.dll` and `0Harmony.dll` in that directory too. F6 toggles
fishing after you enter a world with a rod and bait; F7 detaches, including at the
main menu when updates are running. Insert opens/closes the ImGui menu inside
Terraria's window. The Halloween theme pairs orange accents with black panels and
Windows-hinted Segoe UI rendered on the full backbuffer. A slim "Spooky Fisher"
title bar replaces the large banner; the wider menu has small, rounded navigation
buttons with centered text on the left. Fishing opens by default with start/stop controls,
live status, rod/bait details, saved aim, bobbers, cast failures, and session retrievals.
Click Start fishing, then cast once into the water: it saves that cast's aim and
takes over automatically. Cancel start or F6 cancels the pending start. F6 also
retains its instant start at the current cursor position. Mouse input reaches Terraria outside the panel while it
is open; panel interactions and Console typing stay with ImGui.
The Console tab displays native and managed logs, with filtering,
copy, clear, and auto-scroll. File logs remain beside the DLLs; no console window
is created by the injected DLL.

The menu uses Terraria's XNA graphics device. Dear ImGui is vendored under
`third_party/imgui/`; no additional UI DLLs or NuGet packages are required. Build
both native and managed projects, and restart Terraria before testing rebuilt DLLs.

Detachment removes hooks but cannot unload the managed assembly from Terraria's
default AppDomain. Restart Terraria to load rebuilt managed code. Initialization
or cleanup failures can intentionally leave the native DLL resident; inspect logs
and restart before retrying.

## Development

```powershell
msbuild tests/SpookyFisher.Core.Tests/SpookyFisher.Core.Tests.csproj /p:Configuration=Debug
.\tests\SpookyFisher.Core.Tests\bin\Debug\SpookyFisher.Core.Tests.exe
```

These tests cover queue behavior and the Windows control-event protocol without
loading Terraria. In-game verification is still required for casting, retrieval,
input restoration, world transitions, and detachment.

The fishing state-machine checks use deterministic game stubs and production fishing code:

```powershell
msbuild tests/SpookyFisher.Fishing.Tests/SpookyFisher.Fishing.Tests.csproj /p:Configuration=Debug
.\tests\SpookyFisher.Fishing.Tests\bin\Debug\SpookyFisher.Fishing.Tests.exe
```

They cover arming without automatic input, confirming the first manual cast, ignoring
UI clicks/existing bobbers/other players, cancellation, rod changes, missing bobbers,
bait loss, repositioning before casting, and F6/inverted-gravity aim.

Check Harmony hook installation against your installed game after changing patches:

```powershell
msbuild tests/SpookyFisher.Hooks.Tests/SpookyFisher.Hooks.Tests.csproj /p:Configuration=Debug
.\tests\SpookyFisher.Hooks.Tests\bin\Debug\SpookyFisher.Hooks.Tests.exe bin/Win32/Debug/SpookyFisher.Managed.dll "D:\path\to\Terraria\Terraria.exe"
```

This loads the assemblies in a separate test process and installs/removes every
Harmony hook without starting Terraria or attaching to the running game. It catches
patch-signature errors that compilation and the native UI tests cannot detect,
including requesting a return value from Terraria's void `MouseInput()` method.

After building the solution, run the XNA graphics checks:

```powershell
msbuild tests/SpookyFisher.UI.Tests/SpookyFisher.UI.Tests.csproj /p:Configuration=Debug
.\tests\SpookyFisher.UI.Tests\bin\Debug\SpookyFisher.UI.Tests.exe
```

They use a hidden window and a real XNA device, checking Insert/key-repeat handling,
rendering, native/managed ABI, graphics state restoration, device reset, and safe
window-hook removal. They also check mouse pass-through, panel click/drag isolation,
independent keyboard capture, Console typing, and start/stop/detach actions. A preview
is saved to the test output's `menu-preview.png`.
The checks also cover routing the overlay to the backbuffer when a reduced scene
target/viewport is active, restoring those bindings, and rebuilding hinted fonts
for 2x rendering density.

If a running Terraria instance locks the build outputs, build without interrupting it:

```powershell
msbuild SpookyFisher.sln /m /p:Configuration=Debug /p:Platform=x86 /p:OutDir="$PWD\bin\Validation\Debug\"
msbuild tests/SpookyFisher.UI.Tests/SpookyFisher.UI.Tests.csproj /p:Configuration=Debug /p:NativeUiDirectory="$PWD\bin\Validation\Debug"
```

Restart Terraria before injecting that build; a previously loaded managed assembly
remains in the game's default AppDomain after detach.

See [the design notes](docs/framework-design.md) for the refactor rationale,
and [the menu guide](docs/imgui-menu.md) for rendering, input, logging, and adding tabs.
