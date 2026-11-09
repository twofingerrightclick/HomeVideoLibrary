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

Import probes actual media, remuxes video/audio/subtitle/attachment streams to MKV with metadata and chapters, and falls back to H.264/AAC/SRT conversion when stream copying fails. Bitmap subtitles or other unsupported streams can still cause conversion to fail; the source is preserved and the failed output is removed. Existing Matroska files are copied byte-for-byte to preserve all embedded tags and attachments. Before publication, import checks output duration and stream count, then decodes all video/audio streams and compares the observed playback duration to the source duration. This catches truncated files whose headers still declare their original length, and adds a full playback scan to import time. Conversion and playback validation each have a 24-hour timeout; probe has a 60-second timeout. All stages support cancellation.

The folder contains `library.sqlite3`, `Videos/` with unique MKV names, and `.cache/` for disposable extracted previews. SQLite stores catalog paths and source-derived recording dates. Albums, search, clip editing, and the final VHS interface are added in subsequent stages.

## Play and annotate

Double-click a video, or select it and press Enter, to open the embedded player. Play/pause, seek with the timeline (or the Seek button), adjust volume, and toggle **Video details**. Press **M** outside a text field to add a chapter at the current position. The inspector edits video title/description, tags (one per line), optional ISO recording date, and chapter start/title/description/tags. Chapters have **Go to chapter** and **Remove** actions.

**Capture current frame** chooses a thumbnail at the playback position, up to twenty. **Save changes to MKV** embeds the edits and images in the video. Save stops/releases playback before replacing the file, then restores position and the prior play/pause state. Errors retain dirty edits. **Cancel current operation** cancels loading, saving, or frame extraction. Returning to the library or closing with unsaved changes offers Save, Discard, or Keep editing.

Playback uses the embedded **LibVLC 3 native engine** through LibVLCSharp; installing or launching the VLC desktop app is not a product requirement. Deployments can bundle a matching native runtime and its plugins. For local development:

- Set `HOME_VIDEO_LIBRARY_LIBVLC` to the native library directory for the app's architecture. On Windows this folder contains `libvlc.dll`, `libvlccore.dll`, and a `plugins` directory; distribute the native runtime alongside the app or point to an existing matching runtime.
- On macOS, an explicit runtime path takes precedence. Otherwise the app can use `/Applications/VLC.app/Contents/MacOS/lib` when present. It discovers the matching sibling/nested plugins directory before initializing native LibVLC. Existing startup `VLC_PLUGIN_PATH` values are respected.
- On Linux, install your distribution's LibVLC 3 engine and video/audio plugins, or set the explicit native directory for a bundled runtime. `VLC_PLUGIN_PATH` can identify a nonstandard plugin directory.

Missing/incompatible runtimes produce an actionable message in the player; importing and embedded metadata remain available. See [VideoLAN's LibVLCSharp requirements](https://docs.videolan.me/libvlcsharp/docs/getting_started.html) for native deployment requirements. Native video occupies its own surface, so controls remain outside its bounds, following the [Avalonia integration guidance](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp.Avalonia/README.md).

Run focused editor-state tests with `dotnet test tests/HomeVideoLibrary.Tests --filter FullyQualifiedName~EditorTests`. Native playback was smoke-tested on macOS ARM64; Windows/Linux runtime and window integration still need platform verification.

## Embedded metadata service

Install MKVToolNix (`brew install mkvtoolnix` on macOS) for the new metadata service. Its tools are discovered on PATH or in the standard macOS Homebrew directories; `MediaTools` also allows explicit executable paths.

`MatroskaMetadataService(catalog, tools)` implements `IVideoMetadataService`:

- `ReadAsync(video)` reads the MKV's title, description, repeated keyword tags, recorded date, default edition's top-level chapters, chapter descriptions/tags, and chosen JPEG attachments.
- `CaptureThumbnailAsync(video, position)` extracts a JPEG into disposable cache and returns its ID, timestamp, and image path. It does not write the MKV; callers can use it for previews or explicitly include the image in a later save.
- `SaveAsync(video, metadata)` stages a copy, edits standard Matroska XML tags/chapters and attachments, rereads and verifies the result, then atomically replaces the original. The metadata model accepts up to twenty timestamped thumbnails and unique nonzero chapter UIDs.

Descriptions, titles, tags, recording dates, chapter UIDs/text/tags, JPEGs, and the versioned thumbnail timestamp/hash manifest are embedded in MKV. No SQLite record is needed to recover them. Unknown tags, other editions, nested chapter content, and external attachments are preserved. The editing model exposes the default edition's top-level chapters; removing a chapter that contains nested external chapters is explicitly refused. Files with unsupported structures fail safely instead of silently dropping data. Per-file reads and writes are serialized within the application; external programs should close a file before saving metadata.

Run only metadata service boundary tests with:

```sh
dotnet test tests/HomeVideoLibrary.Tests --filter FullyQualifiedName~MetadataTests
```

## Structure

- `src/HomeVideoLibrary.Core`: domain types independent of UI and persistence.
- `src/HomeVideoLibrary.Infrastructure`: database, file, and media implementations.
- `src/HomeVideoLibrary.Desktop`: Avalonia views, view models, and application composition.
- `tests/HomeVideoLibrary.Tests`: focused behavioral tests.

Follow [CODING_STANDARDS.md](CODING_STANDARDS.md). Original source videos are preserved; content metadata belongs in MKV, while SQLite owns catalog locations and albums.
