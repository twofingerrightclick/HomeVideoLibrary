# Development guide

Build, run, test, and understand Home Video Library. For a product overview, see the [README](README.md).

## Build and run

Install a .NET 10 SDK. On Linux, also install the system libraries required by Avalonia desktop (including X11 and fontconfig). Restore packages once with network access:

```sh
dotnet restore HomeVideoLibrary.sln
dotnet build HomeVideoLibrary.sln --no-restore
dotnet run --project src/HomeVideoLibrary.Desktop
```

Run the tests (the media tests need FFmpeg, ffprobe and MKVToolNix on PATH, or in a standard Homebrew bin directory on macOS):

```sh
dotnet test HomeVideoLibrary.sln
dotnet test tests/HomeVideoLibrary.Tests --filter FullyQualifiedName~ImportTests
```

`HomeVideoLibrary.Tests` covers the services and view models, `HomeVideoLibrary.UI.Tests` the component library, and `HomeVideoLibrary.Desktop.UiTests` the app's views in a headless Avalonia session. The macOS-only test that drives the real Trash is skipped unless `HVL_REAL_TRASH=1`.

With no library open, the shelf offers two keys. **CREATE LIBRARY** asks where to put the library and creates a `HomeVideoLibrary` folder there; if the chosen folder already is a library or is itself named `HomeVideoLibrary`, it opens that folder instead, and if it holds a `HomeVideoLibrary` folder that is not a library, that folder is left alone and the library goes in `HomeVideoLibrary 2`. **OPEN LIBRARY** opens a library folder you choose, as the library folder's **CHOOSE…** in **Settings** does. Then use **IMPORT VIDEOS** to copy media into it. The library reopens on the next launch; when its folder is unavailable (a disconnected drive, say) or it cannot open, the shelf names the folder and offers the same two keys. Sources remain untouched. Imports run as background jobs listed behind the **Activity** key at the bottom right, where each can be cancelled; a cancelled import keeps only previously completed files. Closing the window with jobs running asks whether to wait for them, cancel them, or keep the app open.

Install FFmpeg and ffprobe for import (`brew install ffmpeg` on macOS; use your Linux package manager or Windows FFmpeg distribution elsewhere). Install MKVToolNix for metadata editing, trim, split and merge, and native libVLC for embedded playback (on macOS, a local VLC installation supplies libVLC; the VLC GUI is not used as the player).

Import probes the source and skips it when the same file (by a content fingerprint, not its name) is already in the library. Matroska sources are copied byte-for-byte, keeping all embedded tags and attachments, unless they need a lossless mkvmerge rewrite (WebM, no seek index, a timeline that does not start at zero). Other sources are remuxed with ffmpeg: every stream Matroska can hold is copied; audio it cannot hold becomes FLAC (PCM, ALAC) or AAC 192 kbit/s, text subtitles become SRT, and video is re-encoded with libx264 (`-preset medium -crf 18`) only as a last resort, after ffmpeg rejects the earlier attempts. Cover art, data tracks and bitmap subtitles ffmpeg cannot convert are dropped with a warning. Before publication, import checks output duration and stream count, then decodes every video and audio stream and compares the decoded duration to the source's. This catches truncated files whose headers still declare their original length, and adds a full playback scan to import time. Damaged frames are reported as a warning and the video is still imported. The conversion stage has a 24-hour timeout, probing 5 minutes. All stages support cancellation, and on failure the source is preserved and the staged output removed.

The folder contains `library.sqlite3`, `Videos/` with unique MKV names, and `.cache/` for disposable data (scratch files, extracted previews and the shelf's hover strips). SQLite owns catalog paths and albums; the library reads each video's title, description, tags and chapters from its MKV into an in-memory model that search and the shelf use, so everything but paths and albums is rebuildable from the MKVs. **Settings** shows a per-file error list and has **REFRESH** (re-read changed files) and **REBUILD FROM MKVS** (re-read every file). Opening a library also refreshes changed files in the background.

## The library

The window has a collapsible drawer (search, All videos, albums, Settings), a toolbar, a selection bar that appears with a selection, and the shelf. The shelf is one virtualized column of VHS spines, one per row in a centered stack, newest first, with a month divider before the first tape of each month and a pill pinned at the top that shows the month at the top of the viewport. Each spine shows the video's description or title, falling back to the imported filename. Click a spine to open the video; check its box, or Cmd/Ctrl- or Shift-click, to select. Arrow keys move between tapes, Space toggles the focused tape and Enter opens it. Layout, sizes and the full shortcut table are in [DESIGN.md](DESIGN.md).

Move the pointer along a spine to scrub through a preview of the video above the pointer. The previews are pre-built hover strips: for each video a low-priority background task captures 20 small frames, one for each twentieth of the video, into `.cache/strips/`. A strip is keyed by the file's media identity, so a metadata save keeps it and a changed file builds a new one. Strips build while a tape is hovered or, when the app is otherwise idle, in the background (not while jobs run); a tape whose strip is not ready yet shows "Preparing preview". Strips are a cache: nothing is written into the MKV, and the thumbnails you choose in the FRAMES tab are not used on the shelf.

## Albums and search

Select videos, then choose **NEW ALBUM** (Cmd/Ctrl+N) in the selection bar to create an album with them, or **ADD TO ALBUM** to add them to an existing one. The **+** key beside ALBUMS in the drawer creates an empty "New album" and opens its name for editing; rows also rename with F2 or from their context menu (Rename, Add selected videos, Delete album). Drag selected tapes onto an album row to add them. Deleting an album never deletes videos.

Open an album and choose **ADD VIDEOS…** to switch the shelf to the videos not already in it. Select some and choose **ADD n**, then **DONE**. **REMOVE FROM ALBUM** removes membership only.

The drawer's search box (Cmd/Ctrl+F; Esc clears it) searches titles, descriptions, video tags, and chapter titles, descriptions and tags, in memory over the library model, within the current album when one is shown. Words match case- and diacritic-insensitively as word prefixes and all must match; if nothing matches, the words are retried as substrings. `"a phrase"` matches as a substring and `tag:name` or `tag:"two words"` requires a video or chapter tag. Results are newest first. A tape whose chapter matched shows a "▸ 12:34 · title" line that opens the video at that chapter.

## Deleting videos

**Move to Trash** (selection bar, context menu, the video page's More menu, Delete or Backspace in the library, ⌘⌫ or Ctrl+Delete anywhere on the shelf) asks first, then moves the MKV files to the system Trash (Recycle Bin on Windows) in a background job. Inside an album, Delete only removes videos from the album. To undo, restore the file from the Trash and import it again: it comes back with its title, date, description, tags and chapters, which live in the MKV, but **not with its albums**, which live in the library database. Libraries on network shares or removable drives have no Trash; the app then asks separately before deleting those files permanently. A video whose file has gone missing offers **Remove from library** instead.

When a library opens, background jobs (in slices, so your own edits and trashes go between them) write the title and recorded date into older videos that only had them in the database, so a video file moved out of the library still describes itself. Only missing tags are added; nothing else in the file changes, and videos imported by this version are never touched. Once every video has been checked, the library records it in `.cache/identity-backfill.done` and the jobs stop appearing.

## Video page

Click a tape (or press Enter) to open its page: the video and a transport deck styled like a VCR (scrub bar, track search, rewind and fast-forward, LCD time and total, MARK, volume, fullscreen), and an inspector on the right that can be hidden (Cmd/Ctrl+\) and resized. The toolbar has **LIBRARY** (Esc), an UNSAVED light, **REVERT**, **SAVE**, and a **More** menu (Add to album, Show file location, Move to Trash).

Hovering the scrub bar shows a tooltip above it with the chapter title, time and a frame; chapters appear as marks on the bar. Fullscreen (F) hides the toolbar and inspector; the deck's controls are drawn by a transparent native window over the video, and hide after 2.5 s without pointer movement while the video plays.

The inspector's tabs switch with Cmd/Ctrl+1 to 5:

- **INFO**: title, description, optional recording date (`1997`, `1997-07` or `1997-07-03`), file details and the albums the video is in.
- **CHAPTERS**: press **M** (or MARK) to add a chapter at the playhead; its row opens in place with the title focused. Click a chapter's start time to play from it; click elsewhere on the row to expand it into an editor for title, start, description and tags (one row open at a time; Esc collapses it). Each row has keys to play from it, edit (F2) and remove (Delete).
- **TAGS**: video tags as chips with suggestions from the rest of the library, and the chapter tags in use, each linking to its chapter.
- **FRAMES**: **CAPTURE** (Shift+C) chooses the frame at the playhead as one of up to twenty thumbnails stored in the MKV; trims, splits and merges carry them along. Click one to play from it.
- **EDIT**: trim, split and merge (below).

Transport keys: Space or K play/pause; Left/Right seek 5 s (Shift: 1 s); J/L seek 10 s; Home/End; [ and ] (or Page Up/Down) previous and next chapter; Up/Down volume; 0 mute; F fullscreen. I and O set the trim in and out points.

Nothing is written until **SAVE** (Cmd/Ctrl+S): it embeds the edited text, chapters and chosen frames in the MKV, verifying the result and rolling the file back on failure. On Windows, playback is released during the save. **REVERT** discards the edits. Leaving the page or closing with unsaved changes asks whether to save, discard or keep editing; errors keep the edits. SAVE is unavailable while a trim, split or merge job is reading the video, and if the file changes outside the app the page says so and SAVE keeps your version.

Playback uses the embedded **LibVLC 3 native engine** through LibVLCSharp; installing or launching the VLC desktop app is not a product requirement. Deployments can bundle a matching native runtime and its plugins. For local development:

- Set `HOME_VIDEO_LIBRARY_LIBVLC` to the native library directory for the app's architecture. On Windows this folder contains `libvlc.dll`, `libvlccore.dll`, and a `plugins` directory; distribute the native runtime alongside the app or point to an existing matching runtime.
- On macOS, an explicit runtime path takes precedence. Otherwise the app can use `/Applications/VLC.app/Contents/MacOS/lib` when present. It discovers the matching sibling/nested plugins directory before initializing native LibVLC. Existing startup `VLC_PLUGIN_PATH` values are respected.
- On Linux, install your distribution's LibVLC 3 engine and video/audio plugins, or set the explicit native directory for a bundled runtime. `VLC_PLUGIN_PATH` can identify a nonstandard plugin directory.

Missing/incompatible runtimes produce an actionable message in the player; importing and embedded metadata remain available. See [VideoLAN's LibVLCSharp requirements](https://docs.videolan.me/libvlcsharp/docs/getting_started.html) for native deployment requirements. Native video occupies its own surface, so Avalonia content cannot draw over it; the fullscreen controls and the scrub bar's tooltip are native windows (see [DESIGN.md](DESIGN.md)), following the [Avalonia integration guidance](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp.Avalonia/README.md).

Native playback was smoke-tested on macOS ARM64; Windows/Linux runtime and window integration still need platform verification.

## Trim, split, and merge

The **EDIT** tab (or **TRIM / SPLIT** in the selection bar, with one video selected) trims or splits the open video. **MERGE** (Cmd/Ctrl+Shift+M, with two or more selected) opens the Merge page. Neither modifies the originals: each produces new library videos.

- **Trim**: set in and out points at the playhead (I, O) or type them as `h:mm:ss.fff`; the range must be at least 0.1 s. **PRECISE** (on by default) cuts at the exact frames by re-encoding only the frames between each cut and its nearest keyframe and copying the rest (matching the source's profile, pixel format and colour); sources where that cannot be done safely fall back to keyframe cuts, as does turning PRECISE off. A keyframe cut snaps outward to the nearest keyframes, so nothing requested is lost.
- **Split** at the playhead saves two videos. It is always lossless, at the keyframe nearest the playhead.
- **Merge** joins the clips in the order you arrange (Move up/down, Alt+Up/Down). Clips whose video and audio formats agree are joined without re-encoding. Otherwise the clips that differ are re-encoded to the format of the others (H.264 or HEVC at CRF 18, scaled down or padded to the target size without enlarging, at the target frame rate), and clips without sound receive silence.

Before any work starts, a plan is computed from the actual file and shown live. The commit button reads **SAVE** when no picture needs real re-encoding (a stream copy, including a precise cut that re-encodes only the frames at the cuts), and **RENDER** when it does (no keyframe inside a trimmed range, or a merge clip that must be converted). The info button next to it says in plain words what will happen and why, including the snapped range and any fallback.

The edit runs as a **render job** in the Activity drawer. Render jobs (edits, Trash moves, the identity backfill) run one at a time in the order started; the others show as "Queued (#n)" with a cancel key, and imports run in a separate lane alongside them. A finished job has **OPEN** for its output, and a chime plays when button sounds are on. Keep using the app meanwhile; closing the window asks first. Cancellation or failure removes every uncommitted output; a successful edit publishes all its outputs in one catalog transaction.

Single-source edits keep every track, track tag and attachment from the original. Title, description, tags, recording date, chapters and chosen thumbnails are rewritten on the new timeline: a chapter active at the cut starts at zero, later chapters and thumbnails shift with the cut, and merged clips that lack a chapter at their start get one named after the clip. Outputs are staged and validated (track layout, length, and a decode around every cut or join) before publication.

Run focused editing tests with `dotnet test tests/HomeVideoLibrary.Tests --filter FullyQualifiedName~ClipEditing` (and `~EditPlanning` for the planner).

## Embedded metadata service

Install MKVToolNix (`brew install mkvtoolnix` on macOS) for the metadata service. Its tools are discovered on PATH or in the standard macOS Homebrew directories; `MediaTools` also allows explicit executable paths.

`MatroskaMetadataService(catalog, tools)` implements `IVideoMetadataService`:

- `ReadAsync(video)` reads the MKV's title, description, repeated keyword tags, recorded date, default edition's top-level chapters, chapter descriptions/tags, and chosen JPEG attachments.
- `CaptureThumbnailAsync(video, position)` extracts a JPEG into disposable cache and returns its ID, timestamp, and image path. It does not write the MKV; callers can use it for previews or explicitly include the image in a later save.
- `SaveAsync(video, metadata)` edits the standard Matroska tags, chapters and attachments in place with `mkvpropedit` (which rewrites only metadata elements), rereads and verifies the result, and restores the original metadata if verification fails. The metadata model accepts up to twenty timestamped thumbnails and unique nonzero chapter UIDs.

Descriptions, titles, tags, recording dates, chapter UIDs/text/tags, JPEGs, and the versioned thumbnail timestamp/hash manifest are embedded in MKV. No SQLite record is needed to recover them. Unknown tags, other editions, nested chapter content, and external attachments are preserved. The editing model exposes the default edition's top-level chapters; removing a chapter that contains nested external chapters is explicitly refused. Files with unsupported structures fail safely instead of silently dropping data. Per-file reads and writes are serialized within the application; external programs should close a file before saving metadata.

Run only metadata service boundary tests with:

```sh
dotnet test tests/HomeVideoLibrary.Tests --filter FullyQualifiedName~MetadataTests
```

## Structure

- `src/HomeVideoLibrary.Core`: domain types independent of UI and persistence.
- `src/HomeVideoLibrary.Infrastructure`: database, file, and media implementations (import, metadata, editing, search, Trash).
- `src/HomeVideoLibrary.UI`: the VCR-styled component library and theme tokens, with no dependency on the app.
- `src/HomeVideoLibrary.Gallery`: a showcase of every control in every state (`dotnet run --project src/HomeVideoLibrary.Gallery`).
- `src/HomeVideoLibrary.Desktop`: Avalonia views, view models, playback and application composition.
- `tests/HomeVideoLibrary.Tests`: behavioral tests for services and view models.
- `tests/HomeVideoLibrary.UI.Tests`: component-library tests.
- `tests/HomeVideoLibrary.Desktop.UiTests`: headless tests of the app's views.

Follow [CODING_STANDARDS.md](CODING_STANDARDS.md). Original source videos are preserved; content metadata belongs in MKV, while SQLite owns catalog locations and albums.

## Catalog schema and EF Core migrations

EF Core is the sole schema and migration owner. Runtime catalog, album, cache and import operations still use parameterized SQL. The repository starts with exactly one `InitialCreate` migration: there are no installed libraries to upgrade and no automatic upgrade path for pre-release development catalogs. Create a fresh library directory for development. Keep an old catalog with its original app if its data matters; **the album database is not disposable**. Albums and memberships cannot be reconstructed from MKV metadata. Initialization never deletes, stamps or recreates an unsupported database, and never modifies MKVs.

Restore the pinned local tool before working on schema changes:

```sh
dotnet tool restore
```

Edit `LibraryDbContext` mappings, then generate and review the migration, designer and snapshot together (use an absolute scratch path on your machine):

```sh
dotnet ef migrations add DescribeTheChange --project src/HomeVideoLibrary.Infrastructure --output-dir Migrations -- --database-path /tmp/hvl-schema-development.sqlite3
dotnet ef migrations has-pending-model-changes --project src/HomeVideoLibrary.Infrastructure -- --database-path /tmp/hvl-schema-development.sqlite3
```

The factory requires an explicit absolute database path and never selects a user's library by default. Migration generation does not initialize that path. Do not use `EnsureCreated`, manual schema SQL, `user_version`, or hand-maintained schema versions. Keep the model, generated migrations and snapshot in sync; the catalog migration tests also check for pending model differences. Initialization uses `MigrateAsync` without an enclosing caller transaction, applies local WAL or network DELETE journal policy before migration work and reapplies it afterward. Empty EF bookkeeping left by an interrupted first migration is retryable; an occupied EF migration lock is reported without deleting it or waiting indefinitely. Preserve that database for investigation or create a fresh development library. See [the accepted scope and verification](docs/ef-core-migration-spec.md).
