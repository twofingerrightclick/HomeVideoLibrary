# Home Video Library

A cross-platform C# Avalonia desktop home-video library. The application is being built in sequential stages; see [the product specification](docs/spec.md) and [implementation log](docs/implementation-log.md).

## Build and run

Install a .NET 10 SDK. On Linux, also install the system libraries required by Avalonia desktop (including X11 and fontconfig). Restore packages once with network access:

```sh
dotnet restore HomeVideoLibrary.sln
dotnet build HomeVideoLibrary.sln --no-restore
dotnet run --project src/HomeVideoLibrary.Desktop
```

Run the changed foundation behavior tests:

```sh
dotnet test tests/HomeVideoLibrary.Tests --filter FullyQualifiedName~LibraryPathsTests
```

The foundation currently opens a dark starter window. Library import, playback, and metadata editing are added in subsequent stages. Planned media dependencies are FFmpeg/ffprobe, MKVToolNix, and native libVLC; the bootstrap itself does not require these tools.

## Structure

- `src/HomeVideoLibrary.Core`: domain types independent of UI and persistence.
- `src/HomeVideoLibrary.Infrastructure`: database, file, and media implementations.
- `src/HomeVideoLibrary.Desktop`: Avalonia views, view models, and application composition.
- `tests/HomeVideoLibrary.Tests`: focused behavioral tests.

Follow [CODING_STANDARDS.md](CODING_STANDARDS.md). Original source videos are preserved; content metadata belongs in MKV, while SQLite owns catalog locations and albums.
