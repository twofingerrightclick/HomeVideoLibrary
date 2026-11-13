# Importing a folder

Choose **Import videos → Import folder and subfolders…** to scan a local folder recursively. **Import files…** and the existing import keyboard shortcut still open the file picker.

Discovery runs in the background and appears in Activity. Cancel that scan to stop discovery without queuing its candidates. After scanning, every candidate becomes an ordinary individual import job: each has its own progress, result and cancellation control. A failed video does not stop the other jobs. Original files stay untouched.

The shelf retains the scan's candidate, skipped and inaccessible counts. Expand **Scan details** for paths and reasons. Diagnostics retain the first 50 issues and count additional issues. Skipped items include non-video files, links and excluded library subtrees; an excluded subtree counts as one item because its contents are not scanned. Inaccessible entries are reported separately. Only files with the same candidate extensions as the file picker are queued; the importer checks whether they are valid videos.

The active library, including Videos and its cache/scratch folders, is excluded. Discovery does not follow file or directory links, including a linked selected folder. Select the original folder instead. Library paths opened through symbolic aliases are resolved for exclusion. Sources are checked again before their queued imports run in case an ancestor has become a link.

Selecting the same source folder again uses the existing source-fingerprint duplicate policy: recognized source files report **Already in the library** and new files import normally. This is the existing versioned sampled-byte fingerprint, not a complete checksum or a comparison of footage across encodings. No schema or fingerprint changes are part of folder import.

The app refuses to quit while scans, imports or edits are queued or running. Finish the work, or cancel it in Activity and wait for cancellation to complete, then quit again. A refused quit does not cancel work or schedule an automatic quit.
