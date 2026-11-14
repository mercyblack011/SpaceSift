# SpaceSift Disk Analyzer

A WPF app (.NET 8, 64-bit) for Windows 10 and Windows 11 that finds what fills your disk and helps you free space.

## Tabs
- **Clean Up**: finds junk (Recycle Bin, temp files, Windows Update cache, crash dumps, thumbnail and browser caches, developer caches, old installers, old Windows installs). Safe items are ticked by default; "Check first" items are not.
- **Apps**: installed programs sorted by size. Uninstall runs the program's own uninstaller, then offers to move any leftover folder to the Recycle Bin. "Find leftover folders" lists folders that match no installed app and haven't been used for 30+ days.
- **Tree View**: folder tree, extension list (List / Pie / Donut), category filter and a squarified treemap.
- **File View**: every file, with search, size and date filters.
- Right-click anywhere for Open, Show in Explorer, Copy path, Properties, and Delete (Recycle Bin or permanent).

## Build
```
dotnet build SpaceSift.csproj -c Release
dotnet publish SpaceSift.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
The published `SpaceSift.exe` runs without .NET installed.

If the project sits in a very deep folder, `Directory.Build.props` puts `bin`/`obj` under `%LOCALAPPDATA%\SpaceSift.build` (the WPF XAML compiler fails past 260-character paths).
