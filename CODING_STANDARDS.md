# C# coding standards

- Target .NET 10 and cross-platform Avalonia desktop on Windows, macOS, and Linux.
- Use nullable reference types, file-scoped namespaces, PascalCase public members, and descriptive names. Keep compiler warnings at zero.
- Keep domain models and contracts in Core, filesystem/database/media implementations in Infrastructure, and presentation in Desktop. Core must not depend on Avalonia or storage packages.
- Use async APIs for I/O, pass cancellation tokens through long operations, and suffix asynchronous methods with `Async`. Never block the UI thread waiting for tasks.
- Keep application state in view models. UI event handlers may coordinate platform dialogs and pointer/keyboard interactions; media conversion and persistence belong in services.
- Validate public inputs and protect library boundaries. Use `Path` APIs, parameterized SQL, and `ProcessStartInfo.ArgumentList`; never concatenate user paths into shell commands.
- Treat original source videos as read-only. Stage media changes, verify results, and replace destination files only after success. Cancellation and failure must clean up temporary files.
- MKV is the authority for video descriptions, tags, chapters, and chosen thumbnails. SQLite owns library locations and albums; any metadata search index must be rebuildable.
- Dispose streams, database connections, process handles, and native playback resources. Surface actionable failures to the UI without discarding their diagnostic context.
- Test meaningful changed behavior at service boundaries, especially file safety, metadata round trips, and cancellation. Do not add tests that merely mirror UI markup or implementation details.
- Prefer small concrete modules and explicit dependencies. Add abstractions for real boundaries, not speculative future features.
