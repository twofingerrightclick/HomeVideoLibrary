# Folder import and duplicate detection

Research date: 2026-11-12. Scope: source-based investigation and implementation recommendation; no runtime behavior changed.

## What already exists

The app already remembers imported sources. [VideoImporter](../src/HomeVideoLibrary.Infrastructure/VideoImporter.cs) computes a versioned source fingerprint before probing, converting, or copying. It returns `AlreadyInLibrary` when that fingerprint belongs to a video whose library file is not definitely absent. Renaming a source or changing its filesystem timestamp does not change its fingerprint. [ImportTests](../tests/HomeVideoLibrary.Tests/ImportTests.cs) covers repeat imports, renamed copies, missing destinations, inaccessible destinations, and same-size recordings with different middle content.

The fingerprint is stored in SQLite's `videos.source_fingerprint`, protected by a unique index, and also embedded into the imported MKV as `HVL_SOURCE_FINGERPRINT`. The original filename is embedded separately as `HVL_ORIGINAL_FILENAME`. [LibraryDbContext](../src/HomeVideoLibrary.Infrastructure/LibraryDbContext.cs), [LibraryCatalog](../src/HomeVideoLibrary.Infrastructure/LibraryCatalog.cs), and [VideoImporter](../src/HomeVideoLibrary.Infrastructure/VideoImporter.cs) provide these behaviors already. A folder-import feature that reuses this policy needs no new database table or migration.

The current `v2:` fingerprint hashes the file length, first and last 1 MiB, and eight distributed 64 KiB samples using SHA-256. It reads at most about 2.5 MiB per file, even for a very large recording. This is a sampled fingerprint, not a full-file checksum: two equal-length files differing only outside the sampled regions will match. That limitation follows directly from [ComputeFingerprintAsync](../src/HomeVideoLibrary.Infrastructure/VideoImporter.cs). Filenames are weaker identifiers: different recordings can share a camera-generated name, while identical files can be renamed.

## Recommended first version

Add **Import folder…** beside the existing file import action. Reuse the existing [folder picker](../src/HomeVideoLibrary.Desktop/Views/WindowInteraction.cs) and feed discovered videos into [ImportViewModel.Enqueue](../src/HomeVideoLibrary.Desktop/ViewModels/ImportViewModel.cs). That already schedules one file at a time in the import lane and publishes each success independently.

- Discover videos recursively off the UI thread, with cancellation. Use the existing picker video extensions as the starting candidate list; the importer remains responsible for determining whether each candidate is valid media.
- Exclude the active library directory, including its scratch/cache and `Videos` children. Do not follow directory links in the first version. Deduplicate discovered paths and report inaccessible folders rather than silently hiding them.
- Display scan/import progress and a batch summary: imported, already in library, failed, skipped. One failed video should not discard the rest. Cancelling a batch should cancel both discovery and its remaining queued jobs.
- Keep original files untouched and retain the current sampled fingerprint policy for this first version, documenting its scope. Re-selecting the same source folder then imports new source files and skips recognized ones, without persisting an additional folder-history ledger.

This is primarily folder discovery and batch UX work; media conversion and duplicate storage are already implemented.

## Identity boundaries and legacy libraries

The identifier describes the **original source bytes**, not semantic video content. Remuxing MP4 to MKV, encoding again, or changing tags may change the candidate's byte fingerprint. The imported destination is deliberately written with metadata after source fingerprinting. An existing [test](../tests/HomeVideoLibrary.Tests/ImportTests.cs) explicitly expects importing that generated MKV to create another video. Folder scanning must therefore avoid importing the library into itself. Matching the same footage across containers or encodings is a separate, substantially larger feature.

Keep source fingerprints stable when users edit MKV descriptions or chapters; do not recalculate them from edited library files. Normal metadata writes preserve unrelated tags in [MatroskaMetadataXml](../src/HomeVideoLibrary.Infrastructure/MatroskaMetadataXml.cs). The separate media identity cache hashes track properties and duration, so it is not suitable as a duplicate-content checksum. [MatroskaMetadataService](../src/HomeVideoLibrary.Infrastructure/MatroskaMetadataService.cs)

Older rows can have no source fingerprint. The original source fingerprint cannot reliably be reconstructed from a converted or metadata-edited destination. An embedded source tag could support a deliberate catalog recovery path when available, with uniqueness checks; otherwise leave the row unknown rather than inventing a filename match. Existing fingerprint versions must remain distinguishable when changing the algorithm.

## Optional stronger exact-source detection

For a stricter requirement, stream a full-file SHA-256 before automatic skipping and store it as a separate versioned original-source identity. File size affects read time, not the need to load the whole video into memory. The tradeoff is a complete source read, especially noticeable on network drives. Hashing only during conversion cannot avoid doing that work on a later duplicate candidate.

Retain sampled fingerprints as a fast candidate filter, but allow several records with the same sampled value; the current unique index would otherwise incorrectly reject a sample collision. Existing rows lacking a full-source hash cannot be silently upgraded from destination bytes: recover from originals where available or treat them as legacy matches. This upgrade needs a schema migration and compatibility policy; it need not block basic folder import.

## Safety and verification

[VideoImporter](../src/HomeVideoLibrary.Infrastructure/VideoImporter.cs) already checks the source size/write timestamp around conversion, cleans staging in `finally`, and handles a unique-index race by returning the winning import. [LibraryPublisher](../src/HomeVideoLibrary.Infrastructure/LibraryPublisher.cs) commits files with recovery support. Preserve these paths and test folder cancellation, inaccessible children, nested folders, duplicate files within a batch, repeat selection, active-library exclusion, and mixed success/failure. If adding stronger hashing or a separate preflight duplicate scan, revalidate source stability before accepting its result and keep the final database constraint as the publication guard.

Validation during this investigation: four existing tests passed: `ReimportingTheSameSourceReturnsTheExistingVideo`, `FingerprintTellsApartFilesThatDifferOnlyInTheMiddle`, `ASecondRowWithTheSameSourceFingerprintIsRefusedByTheCatalog`, and `ASourceWhoseVideoFileIsGoneImportsAgainAndTakesTheFingerprintOver`. Folder behavior remains proposed and has not been implemented or tested.
