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

Install FFmpeg and ffprobe for import (`brew install ffmpeg` on macOS; use your Linux package manager or Windows FFmpeg distribution elsewhere). Install MKVToolNix for metadata editing and native libVLC for embedded playback (on macOS, a local VLC installation supplies libVLC; the VLC GUI is not used as the player).

Import probes actual media, remuxes video/audio/subtitle/attachment streams to MKV with metadata and chapters, and falls back to H.264/AAC/SRT conversion when stream copying fails. Bitmap subtitles or other unsupported streams can still cause conversion to fail; the source is preserved and the failed output is removed. Existing Matroska files are copied byte-for-byte to preserve all embedded tags and attachments. Before publication, import checks output duration and stream count, then decodes all video/audio streams and compares the observed playback duration to the source duration. This catches truncated files whose headers still declare their original length, and adds a full playback scan to import time. Conversion and playback validation each have a 24-hour timeout; probe has a 60-second timeout. All stages support cancellation.

The folder contains `library.sqlite3`, `Videos/` with unique MKV names, and `.cache/` for disposable extracted previews. SQLite owns catalog paths and albums; metadata/search projections are rebuildable from MKV. The library presents virtualized VHS spines with handwritten descriptions and a scroll-linked month/year marker. Click a label to play; check boxes or modifier-click to select videos. Space toggles a keyboard-focused tape and Enter plays it. Move over a label to preview chosen thumbnails or disposable cached frames; hovering never saves a thumbnail into the MKV. The sidebar and playback inspector can be collapsed.

## Trim, split, and merge

Select one or more library videos and choose **Edit selected clips**. With one video, enter start/end positions in seconds and save a trimmed MKV, or choose a split position and save both parts. Ranges must be ordered, within the source duration, and at least 0.1 seconds long. With two or more videos, use **Move selected up/down** to set their explicit output order, then **Save merged MKV**. The editor shows the output format and supports progress/cancellation.

Every operation creates new library videos; originals are never modified. Single-source trim/split preserves source dimensions, display aspect, and frame timing, adjusting odd dimensions only as required for H.264. Merge uses the first ordered clip's dimensions/sample aspect/frame rate, scales down or pads other clips to preserve display aspect, and avoids enlarging smaller sources. Video is reencoded as H.264 CRF 18 for accurate cuts; audio becomes stereo 48 kHz AAC. Silent clips receive silence. This is a primary video/audio workflow: additional tracks, subtitles, external attachments, and alternative chapter editions remain in the originals rather than being copied into derivatives.

Descriptions, arbitrary tags, recording date, default-edition chapters, and chosen JPEG thumbnails are retained and shifted to the output timeline. A chapter active at the cut point starts at zero; later chapters/thumbnail timestamps shift with the cut or merge offset. Merge chapter UID collisions receive new UIDs. Each derivative retains up to the first twenty chosen thumbnails on its timeline.

Outputs are staged, fully decoded/validated, and receive their remapped embedded metadata before publication. Split pairs register in one SQLite transaction; cancellation or failure removes every uncommitted output. Successful outputs are indexed automatically. Closing during an operation is deferred until it finishes or is cancelled.

Run focused editing tests with `dotnet test tests/HomeVideoLibrary.Tests --filter FullyQualifiedName~ClipEditingTests`.

## Albums and search

Select multiple videos with Command/Ctrl or Shift. Enter an album name and choose **Create album** to create it with the selected videos (or create an empty album without a selection). Use the destination album selector and **Add selected to album**, or drag selected videos onto an album name. Dropping onto the drawer adds them to the current album, falling back to the destination selector. Rename and **Delete album only** affect organization; deleting an album never deletes videos.

Within an album, **Add videos / show album** switches to videos not already in that album. Select videos and choose **Add selected here**. **Remove selected from album** removes membership only.

## Deleting videos

**Move to Trash** (selection bar, context menu, the video page's More menu, Delete or Backspace in the library, ⌘⌫ or Ctrl+Delete anywhere on the shelf) asks first, then moves the MKV files to the system Trash (Recycle Bin on Windows) in a background job. Inside an album, Delete only removes videos from the album. To undo, restore the file from the Trash and import it again: it comes back with its title, date, description, tags and chapters, which live in the MKV, but **not with its albums**, which live in the library database. Libraries on network shares or removable drives have no Trash; the app then asks separately before deleting those files permanently. A video whose file has gone missing offers **Remove from library** instead.

When a library opens, background jobs (in slices, so your own edits and trashes go between them) write the title and recorded date into older videos that only had them in the database, so a video file moved out of the library still describes itself. Only missing tags are added; nothing else in the file changes, and videos imported by this version are never touched. Once every video has been checked, the library records it in `.cache/identity-backfill.done` and the jobs stop appearing.

The drawer searches MKV titles, descriptions, video tags, and chapter titles/descriptions/tags. Search matches Unicode word prefixes; punctuation and words such as OR are treated as ordinary input, without exposing SQL or FTS operators. Results favor relevance and then recording date. Empty search displays the current library/album chronologically. Library labels use embedded descriptions or titles, falling back to the imported filename.

The FTS5 index is a disposable SQLite cache. Opening a library refreshes changed files in the background; imports and metadata saves update their entries. **Refresh search index** detects external edits using catalog identity plus file size/modification time. **Rebuild index from MKVs** rereads every file. Cancellation preserves previously indexed entries and completed updates; another refresh resumes stale entries. Refresh reports unreadable files without removing their source media. Changes that deliberately preserve both file size and modification time require a forced rebuild. Album filtering also applies while searching.

Run only new album/search boundary tests with `dotnet test tests/HomeVideoLibrary.Tests --filter FullyQualifiedName~AlbumsAndSearchTests`. The native drag/drop handlers compile; manual platform interaction verification remains part of the final UI pass.

## Play and annotate

Select a video and choose **Open selected video**, double-click it, or press Enter to open the embedded player. Play/pause, seek with the timeline (or the Seek button), adjust volume, and toggle **Video details**. Press **M** outside a text field to add a chapter at the current position. The inspector edits video title/description, tags (one per line), optional ISO recording date, and chapter start/title/description/tags. Chapters have **Go to chapter** and **Remove** actions.

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
