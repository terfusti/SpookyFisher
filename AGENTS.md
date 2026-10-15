# Repository Guidelines

## Project Structure & Module Organization

`SpookyFisher.sln` contains three Windows projects:

- `src/SpookyInjector/src/main.cpp`: executable that loads the native DLL into Terraria.
- `src/SpookyFisher.Native/src/dllmain.cpp`: C++ bridge to Terraria's existing CLR v4, including control events and DLL lifecycle management.
- `src/SpookyFisher.Managed/Entry.cs`: C# entry point, Harmony patches, fishing logic, and logging. Assembly metadata lives in `Properties/AssemblyInfo.cs`.

Build artifacts go under `bin/`; restored dependencies go under `packages/`. Both are ignored. There are no dedicated test or asset directories.

## Build, Test, and Development Commands

Use a Visual Studio Developer Command Prompt with MSBuild, MSVC toolset `v145`, the Windows 10 SDK, and .NET Framework 4.7.2 targeting support.

- `nuget restore SpookyFisher.sln`: restore Lib.Harmony 2.4.3 from `packages.config`.
- `msbuild SpookyFisher.sln /m /p:Configuration=Debug /p:Platform=x86`: build native Win32 binaries and the managed library into `bin/Win32/Debug/`.
- `msbuild SpookyFisher.sln /m /p:Configuration=Release /p:Platform=x86`: build Release outputs.
- `.\bin\Win32\Debug\SpookyInjector.exe`: run the injector after starting Terraria.

Before building, adjust Terraria/XNA reference paths in the managed `.csproj` and the hardcoded DLL path in `main.cpp`. Match native architecture to Terraria; managed outputs currently use `bin/Win32/` even for x64 solution builds. Keep `SpookyFisher.Managed.dll` and `0Harmony.dll` beside `SpookyFisher.dll`.

## Coding Style & Naming Conventions

Use C++20 and the existing .NET Framework C# syntax. Match surrounding formatting: four spaces in managed code and the native bridge, tabs in the injector. C# uses braces on separate lines; bridge functions use opening braces on the declaration line. Use PascalCase for types and methods and camelCase for locals. No formatter or linter is configured.

## Testing Guidelines

No automated framework or coverage threshold exists. Build Debug and Release for the affected architecture. Manually verify attachment, F6 toggling, casting/retrieval, blocked states, and F7 detachment. Inspect `SpookyFisher.bootstrap.log` and `SpookyFisher.managed.log` beside the DLLs. Record Terraria version, configuration, and results in the PR.

## Commit & Pull Request Guidelines

History uses short imperative subjects with punctuation, such as `Add project files.` Follow that style. Keep commits focused. PRs should describe behavior changes, link relevant issues, and include build/manual validation results and logs or screenshots when useful.
