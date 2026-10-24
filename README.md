# Flux Studio

Flux Studio is a WinUI 3 / Windows App SDK desktop application for editing Flux source files. The project is a single packaged application with MSIX support.

## Current editor features

- Flux syntax highlighting for keywords, built-in types, literals, comments, operators, and preprocessor directives.
- Automatic indentation on Enter, four-space Tab insertion, and Shift+Tab line unindent. A newly typed `if … then`, `for … then`, or colon-based block header automatically gets an aligned `end` when Enter is pressed at the end of the document.
- Completion suggestions for language keywords, types, built-in I/O functions, directives, local identifiers, and common block templates. Press `Ctrl+Space` to request suggestions. Exact keyword matches such as `end` remain usable; Enter only accepts a completion when it would actually change the current word.
- Real-time diagnostics are provided by the selected native `Flux.Core` interpreter, launched once per second in syntax-check mode with the current unsaved editor buffer sent through stdin. The UI only rebinds the Error List when the parsed diagnostic set actually changes; unchanged results do not refresh the list. Error spans receive a red wavy underline. The check uses the selected interpreter path from Settings and never executes the document.
- Click a diagnostic to jump to its line and column.
- Editor settings use a dedicated modal-style `SettingsWindow`. WinUI 3 does not provide WPF’s built-in synchronous `Window.ShowDialog()`, so `SettingsWindow.ShowDialog()` is an awaitable wrapper that disables the owner window until the settings window closes. Settings include theme, font family/size, indentation, automatic `end` insertion, completion, and the native runtime path/working directory. `Ctrl` + mouse wheel zooms the editor font. Zoom changes are saved immediately and synchronized with the font-size field in Settings. Changing only the indentation width updates the preference without reformatting the whole document. `F5`, `Ctrl+F5`, and the toolbar Run command call the selected interpreter directly as `flux <current-file-path>` (no `run` subcommand); the bottom panel switches to Output and remains there after execution ends. `read` and `pause` requests are presented in WinUI dialogs and fed back into the native process. The status bar shows encoding, line ending, document line/character counts, and caret position. When there are no diagnostics, the Error List shows a centered green check plus gray “未找到相关问题” empty-state placeholder; it is not a list item and has no card frame.
- Open/save `.flux` and `.flpkg` files, open a project from `flux.json`, and use Find (`Ctrl+F`). The MSIX manifest registers `.flux` as “Flux 源文件” and `.flpkg` as “Flux 外部包文件”; opening either file through File Explorer activates Flux Studio and loads the selected file. In WinUI 3, file activation is detected in `App.OnLaunched` via `Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs()` and `ExtendedActivationKind.File` (the UWP-only `Application.OnFileActivated` override is not used). Windows may ask the user to choose Flux Studio as the default app the first time.

## Native Flux interpreter

The `Flux.Core/` folder is a fully independent, cross-platform C++17 interpreter project with its own `Flux.Core.sln`, `Flux.Core.vcxproj`, and `CMakeLists.txt`. It builds `Flux.Core/bin/flux.exe` on Windows (or `Flux.Core/bin/flux` on Linux/macOS). The interpreter does not use .NET. The WinUI app launches that same executable as a child process and streams stdout/stderr into the Output tab. `FluxStudio.sln` does not build Flux.Core; build the runtime separately, then build Flux Studio to copy the prebuilt executable into the MSIX package.

Build it with CMake and a C++17 compiler:

```sh
cmake -S Flux.Core -B Flux.Core/build
cmake --build Flux.Core/build --config Release
```

The standalone executable is placed in `Flux.Core/bin/flux.exe` on Windows (or `Flux.Core/bin/flux` on Linux/macOS). Open `Flux.Core/Flux.Core.sln` and build it separately in Visual Studio, or use CMake. Then rebuild `FluxStudio.sln`; if `Flux.Core/bin/flux.exe` exists, the app project copies it into the output and MSIX package as `Runtime/bin/flux.exe`. If it does not exist, Flux Studio still builds but will report that the interpreter is missing when Run is invoked. The run command contract is `flux <file-path>`: an absolute path, path relative to the working directory, or a file name. The `run` subcommand is not used. The IDE uses the additional internal `flux --check-stdin <context-file-path>` mode to check the live, unsaved editor buffer without executing it; ordinary CLI invocation remains `flux <file-path>`.

The first-stage runtime supports source lexing/parsing, variables and assignments, built-in primitive casts, arithmetic/comparison/logical operations, `print`/`printf`/`printfn`, `read` and `pause`, `if`/`else if`/`else`, condition loops (`for`), `break`/`next`, method declarations/calls, `return`, `$replace`, and relative `$copy`. `$open "System"` is accepted, but external module loading is not implemented. Class/interface/object semantics, full static type analysis, the complete error-handler model, accurate original-file source mapping for copied files, and some advanced conversions are not yet implemented. Unsupported features fail explicitly instead of being silently treated as successful execution. See `Flux.Core/README.md` for the exact prototype boundary.

## Flux language reference

Flux syntax highlighting and the native interpreter's lexer/parser checks follow the language draft and Windows desktop specification in the official source repository:

- [Flux language specification](https://github.com/MsintX/Flux/blob/main/Flux%E8%AF%AD%E6%B3%95%E8%A7%84%E8%8C%83.md)
- [Flux desktop specification](https://github.com/MsintX/Flux/blob/main/Flux_Desktop%E8%A7%84%E8%8C%83.md)

The IDE delegates diagnostics to `Flux.Core` once per second using its parser/preprocessor check mode. The current interpreter checks lexical validity, delimiter and grammar errors, supported preprocessor directives, and other parser diagnostics without executing the program. Full static type checking, overload resolution, dependency resolution, and runtime validation are not yet implemented. The UI refreshes Error List only when the diagnostic set differs from the previous result.

## Project layout

```text
FluxStudio/                 # Extract this ZIP directly into the target folder
├── FluxStudio.sln
├── FluxStudio.csproj
├── App.xaml
├── App.xaml.cs
├── MainWindow.xaml
├── MainWindow.xaml.cs
├── Package.appxmanifest      # Registers .flux and .flpkg file associations
├── app.manifest
├── Assets/
├── Properties/PublishProfiles/
├── SettingsWindow.xaml       # Modal-style settings window
├── SettingsWindow.xaml.cs
├── SettingsView.xaml         # Settings content and fields
├── SettingsView.xaml.cs
├── Services/                # Flux lexer, diagnostic model, completion and settings models
├── Flux.Core/
│   ├── Flux.Core.sln         # Standalone interpreter solution
│   ├── Flux.Core.vcxproj     # Independent Visual Studio C++ console project
│   ├── Flux.Core.vcxproj.filters
│   ├── Flux.Core.cpp         # Single-file C++17 interpreter
│   ├── CMakeLists.txt        # Cross-platform build configuration
│   └── README.md
├── examples/
├── README.md
└── .gitignore
```

The window XAML and code-behind stay together, and the project file and MSIX manifest live in the same application project root. `FluxStudio.sln` contains only the WinUI app. `Flux.Core/Flux.Core.sln` is independently buildable and produces the command-line interpreter. The same `flux.exe` can be run directly in a terminal or launched by the IDE. There is no separate MSIX packaging project or custom build script.

## Open and run

1. Install Visual Studio with the WinUI application development workload and Windows App SDK tooling. To build `Flux.Core` with its Visual Studio project, also install **Desktop development with C++** (MSVC v145 toolset for Visual Studio 2026).
2. Extract the ZIP into an empty folder, for example `E:\SourceFiles\repo\FluxStudio`. Do not overlay an older extracted project.
3. To use the interpreter from the IDE, open `Flux.Core\Flux.Core.sln` and build `Flux.Core` first. This independently produces `Flux.Core\bin\flux.exe`, which can also be invoked directly from a terminal.
4. Open the root `FluxStudio.sln` and rebuild it. If `Flux.Core\bin\flux.exe` exists, the app project copies it into the output/MSIX package at `Runtime\bin\flux.exe`.
5. In Configuration Manager, make sure `Debug | x64` has both **Build** and **Deploy** selected, then press `F5` to deploy and run the packaged app.
6. To distribute MSIX, use Visual Studio's packaging/publish workflow. Sideloading requires a certificate trusted by the target machine.

The project identity is `FluxStudio`; the user-facing display name is **Flux Studio**.

### Recent behavior fixes

- Closing the settings window correctly re-enables the main window. The native `EnableWindow` return value reports the *previous* state, so it is not used as the new enabled state.
- Running a saved, modified file saves back to its existing path. Running an untitled buffer creates a temporary `.flux` snapshot and does not force a Save As dialog or change the editor document's path.
- The empty “未找到相关问题” state stretches across the error-list panel and centers its icon and text in the available space.

## Visual Studio 2026 启动方式

项目包含 `Properties/launchSettings.json`，启动配置为 `MsixPackage`。请从 Visual Studio 启动配置中选择 `FluxStudio`（MSIX）并按 F5；不要直接运行 `bin\x64\Debug\...\FluxStudio.exe`。直接运行 EXE 会绕过 MSIX 包身份，可能触发 `REGDB_E_CLASSNOTREG (0x80040154)`。


## Flux.Core 多错误诊断

Flux.Core 在执行前会收集词法、语法和预处理指令错误，按源码位置排序后一次输出（最多 100 条），不会遇到第一条语法错误就立即退出。存在这些错误时不会执行文件。运行期间的动态错误仍会在首个失败操作处停止，这是为了避免继续执行产生不可靠结果。
