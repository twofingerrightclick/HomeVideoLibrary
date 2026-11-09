# Implementation log

## Review baseline

The repository was empty at task start. The fixed baseline for review is the empty commit `910d058df3daa51b31ace68f60526d617b98584e`.

## Stage 1: foundation

- .NET 10 solution with Core, Infrastructure, Avalonia Desktop, and xUnit test projects.
- Avalonia 11.3.22 pins the latest available 11.x stable release for compatibility with the planned LibVLCSharp.Avalonia integration.
- Nullable references, compiler warnings as errors, shared formatting guidance, and documented C# standards.
- A dark starter window; no simulated media or nonfunctional feature controls.
- `LibraryPaths` defines the selected library root, SQLite location, media directory, and disposable cache directory.

Later stages add initialization, persistence, conversion, playback, and the final interface. This foundation alone does not implement these features.

Verification: NuGet restore succeeded; `dotnet build HomeVideoLibrary.sln --no-restore -m:1 -nr:false` passed with zero warnings/errors; the focused `LibraryPathsTests` filter passed all three cases. The native window has not yet been visually verified on Windows or Linux.

## Stage 2: library initialization and import

- Added SQLite path catalog, UUID media filenames, guarded relative path resolution, and persisted selection of the library root.
- Basic native folder/file choosers, reopened library list, per-file status/progress, cancel, and surfaced conversion errors.
- FFprobe validates actual video and duration; FFmpeg first remuxes video/audio/subtitles/attachments plus metadata and chapters, then explicitly falls back to H.264/AAC/SRT on failure. Existing Matroska media copies byte-for-byte to retain all Matroska-specific tags, UIDs, and attachments.
- Source files stay untouched. Output is staged and probed before a same-directory atomic rename. Failed/cancelled imports clean up staging; failed database registration rolls back the published file.
- Process execution uses argument lists, bounded stdout/stderr, timeout/cancellation, and process-tree termination. Source creation metadata (or file modification time) is retained for later chronological presentation.
- Native tools were installed separately on this host. Other platforms still require tool installation; Windows/Linux GUI behavior remains unverified.

Verification: solution build passed with zero warnings/errors. The focused `ImportTests` filter passed twelve cases, including real MP4 remux, incompatible MP4 text-subtitle conversion, byte-identical MKV reimport, unique outputs, source preservation, validation rejection, cancellation, process timeout, database rollback, path guards, and settings persistence. Existing foundation tests were not rerun for this stage.

### Stage 2 review fix: reject truncated media with intact headers

Terra found that header probing alone allowed truncated MKV footage to enter the library. Import now decodes every video/audio stream with FFmpeg error escalation before publication and compares the observed decoded timeline against the expected duration (one percent tolerance, with a 250 ms floor). Both the scan and staging cleanup honor cancellation. The full scan adds work proportional to playback decoding; originals remain untouched.

Focused verification passed four tests: a physically truncated MKV whose FFprobe duration still reports twelve seconds, cancellation at playback validation, valid MP4/MKV import preservation, and subtitle-transcode fallback. No unrelated tests were rerun.

## Stage 3: embedded Matroska metadata

- Added `IVideoMetadataService` and concrete metadata/chapter/thumbnail records. Title, description, keywords, recording date, and chapter-targeted descriptions/keywords use Matroska tags; chapters retain stable unsigned 64-bit UIDs.
- MKVToolNix reads and edits XML metadata. Unknown XML tags, track tags, alternative editions, nested chapter content, and external attachments are retained. Editing targets the default edition's top-level chapters; deleting a parent with nested chapters or colliding with another edition's UID is refused.
- Up to twenty JPEG attachments persist alongside a versioned attachment manifest of IDs, timestamps, names, and SHA-256 hashes. Reopening reconstructs cached images from the MKV alone.
- Capture extracts a cancellable JPEG preview to cache without saving metadata. UI preview callers can debounce requests; only an explicit save chooses attachments.
- Safe writes serialize per video, copy to staging, run mkvpropedit, reread and verify metadata/thumbnail hashes plus track identity/duration, then atomically replace. Cancellation and errors leave the original unchanged and clean up staging.
- Focused `MetadataTests` passed five cases: real reopen/roundtrip, external chapter/unknown tag/edition/attachment preservation, limits/positions, failed/pre-cancelled atomic safety, and cancellation after staging begins. No unrelated tests were rerun.

### Stage 3 review fixes: preserve foreign tag targets

Terra found two cases where ownership was too broad: UID-free collection/season tags were treated as video-level fields, and mixed chapter/track targets were treated as exclusive chapter metadata. The editor now owns only UID-free default/50 video-level targets, preserving other hierarchy levels and explicitly named foreign audio targets. Exclusive chapter editing requires exactly one chapter UID and no other UID targets; mixed targets remain untouched even when the chapter is removed. New tags explicitly declare MOVIE/50 or CHAPTER/30.

Target levels were verified against [RFC 9559 section 5.1.8.1.1](https://www.rfc-editor.org/rfc/rfc9559.html#section-5.1.8.1.1): omitted TargetTypeValue defaults to 50, and COLLECTION uses 70. The preservation policy additionally treats explicitly named foreign target types conservatively.

Focused verification passed the two new real-MKV regressions (collection/season/album preservation and chapter-plus-track preservation through edit/removal) plus the directly affected standard metadata roundtrip. No unrelated tests were rerun.

## Stage 4: embedded playback and annotation inspector

- Pinned LibVLCSharp.Avalonia 3.10.2 with the existing Avalonia 11.3.22 stack. `IVideoPlayback` separates asynchronous native lifecycle from the editor state; native callbacks marshal through Avalonia's dispatcher and carry a generation to discard stale updates.
- Added in-app video surface, play/pause, seek, volume, elapsed/total time, back navigation, and optional details sidebar. Text fields have accessible labels, and selected videos open by Enter as well as double-click. Controls remain outside the native video surface.
- Inspector supports title, description, newline-separated arbitrary tags, ISO date, chapter title/start/description/tags/removal/seeking, M marker shortcut outside text fields, chosen frame capture/display/removal, explicit save, dirty state, cancellation, and surfaced errors.
- Save stops native decoding, detaches/disposes playback handles before staged replacement, then restores position and play state. Failed/cancelled saves retain edits. Leaving prompts inline to save, discard, or keep editing; video switching is serialized through leaving the current editor.
- Native discovery honors `HOME_VIDEO_LIBRARY_LIBVLC` and startup `VLC_PLUGIN_PATH`; macOS can use an installed app's runtime as a development fallback. The VLC GUI is optional; only the native engine/plugins are required.
- Applied the diagnosing-bugs skill to a reproducible native startup failure: LibVLC searched the absent `MacOS/lib/vlc/plugins` directory. A shell plugin path made the same harness pass; managed environment changes alone did not. Minimal platform-specific native `setenv` now configures the discovered macOS plugins before LibVLC initialization. Another harness finding fixed stop-before-detach ordering, removing native no-drawable errors.
- Verification: affected Desktop build passed with zero warnings/errors; fifteen focused `EditorTests` passed (dirty state, chapter edits, save release/resume ordering, failure/cancellation retention, shortcut rules, seek bounds). An actual Avalonia VideoView smoke harness on macOS ARM64 confirmed a nonzero native view, displayed video frame statistics, advancing playback, seek, chapter add, frame capture, metadata save, and displayed frames after reopening. No unrelated tests rerun. Windows/Linux native UI still require verification.

### Stage 4 review fixes: scoped exit intent and discoverable playback

Terra found that a deferred window-close intent could survive leaving an editor through Back and later close the application from another video's confirmation. Exit destination now belongs to the view model's editor navigation state. A fresh Back request overrides a window request; Keep editing cancels it; completed editor closure consumes and clears it. The view closes the window only for an explicit returned Window destination.

The library now exposes a visible, keyboard-accessible **Open selected video** action, enabled only with a selection. Selection remains separate from playback for the upcoming album multi-selection flow. Double-click and Enter remain shortcuts.

Three focused `EditorNavigationTests` passed, covering the reported cross-editor close sequence, completed/cancelled exit reset, and selected-action availability. The affected Desktop compiled with zero warnings/errors as part of this run. No unrelated tests were rerun.

## Stage 5: albums and indexed metadata search

- Transactional schema version 2 preserves version 1 video registries and adds albums, foreign-key/cascading membership, and a rebuildable FTS5 cache. Newer schema versions are refused rather than downgraded.
- Albums support create (optionally from selected videos in one transaction), rename/delete, idempotent membership add/remove, and nonmember listing. Album deletion never touches media.
- Search projects MKV title/description/tags and chapter text/tags into an external-content FTS5 index with transactional insert/update/delete triggers. Queries tokenize Unicode letters/marks/numbers into quoted prefixes, treating punctuation/operators as user text. Album inclusion/exclusion filters and relevance/date ordering use parameterized SQL.
- Cached projections include catalog ID, size, modification timestamp, and embedded recording date; unchanged files skip metadata extraction. Each file update commits independently after a complete read and identity recheck. Cancellation/errors preserve the previous cache entry, whose old timestamp remains retryable. Forced refresh reconstructs all entries; deliberate same-size/same-timestamp edits require this path.
- Background refresh has progress/cancellation. Imported videos and explicit MKV saves update the index; save-cache errors report a refresh warning without misrepresenting a committed MKV save. Library/query generations discard stale async results. Library labels now display MKV descriptions/titles, and clearing an embedded date restores the original catalog fallback rather than recycling a cached date.
- Basic UI includes album CRUD, multi-selection, creation from selection, membership controls, in-album nonmember mode, native typed drag/drop onto names/drawer, search, refresh/rebuild, and cancellation. No visual redesign in this stage; native drag/drop awaits final interactive platform verification.
- Six focused `AlbumsAndSearchTests` passed: album/source safety and FK/idempotence, Unicode/prefix/query-syntax search and chapter/tag coverage, cancelled cache refresh, real external MKV edit refresh and immediate post-save indexing/rebuild, version-1 migration, and recorded-date fallback. Only the changed stage's behavior was tested.
