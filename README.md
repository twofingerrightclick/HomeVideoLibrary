# Home Video Library

A cross-platform C# Avalonia desktop home-video library. The application is being built in sequential stages; see [the product specification](docs/spec.md) and [implementation log](docs/implementation-log.md).

## Build and run

Install a .NET 10 SDK. On Linux, also install the system libraries required by Avalonia desktop (including X11 and fontconfig). Restore packages once with network access:

```sh
dotnet restore HomeVideoLibrary.sln
dotnet build HomeVideoLibrary.sln --no-restore
dotnet run --project src/HomeVideoLibrary.Desktop
```

Run focused import behavior tests (requires FFmpeg and ffprobe on PATH, or in a standard Homebrew bin directory on macOS):

```sh
dotnet test tests/HomeVideoLibrary.Tests --filter FullyQualifiedName~ImportTests
```

Choose a library folder in the left sidebar, then use **Import videos…** to copy media into it. The selected library reopens on the next launch. Sources remain untouched. Cancel stops the active import and keeps only previously completed files. To close during an import, cancel first and close after cleanup completes.

Install FFmpeg and ffprobe for import (`brew install ffmpeg` on macOS; use your Linux package manager or Windows FFmpeg distribution elsewhere). MKVToolNix and native libVLC are planned dependencies for later metadata and playback stages.

Import probes actual media, remuxes video/audio/subtitle/attachment streams to MKV with metadata and chapters, and falls back to H.264/AAC/SRT conversion when stream copying fails. Bitmap subtitles or other unsupported streams can still cause conversion to fail; the source is preserved and the failed output is removed. Existing Matroska files are copied byte-for-byte to preserve all embedded tags and attachments. Output duration and video stream count are checked before publication. Conversion has a 24-hour timeout; probe has a 60-second timeout.

The folder contains `library.sqlite3`, `Videos/` with unique MKV names, and `.cache/` for disposable extracted previews. SQLite currently stores catalog paths and source-derived recording dates. Playback, metadata editing, albums, search, and the final VHS interface are added in subsequent stages.

## Structure

- `src/HomeVideoLibrary.Core`: domain types independent of UI and persistence.
- `src/HomeVideoLibrary.Infrastructure`: database, file, and media implementations.
- `src/HomeVideoLibrary.Desktop`: Avalonia views, view models, and application composition.
- `tests/HomeVideoLibrary.Tests`: focused behavioral tests.

Follow [CODING_STANDARDS.md](CODING_STANDARDS.md). Original source videos are preserved; content metadata belongs in MKV, while SQLite owns catalog locations and albums.
