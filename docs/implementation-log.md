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
