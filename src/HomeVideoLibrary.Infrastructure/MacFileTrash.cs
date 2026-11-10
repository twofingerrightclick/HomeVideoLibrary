using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Finder's Trash through <c>-[NSFileManager trashItemAtURL:resultingItemURL:error:]</c>, which puts the file
/// in the Trash of its own volume (so Put Back works) and refuses volumes without one.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacFileTrash : IFileTrash
{
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    // NSCocoaErrorDomain codes from FoundationErrors.h.
    private const nint FileLockingError = 255, FeatureUnsupportedError = 3328;
    private static readonly Lazy<nint> FoundationHandle = new(() => NativeLibrary.Load(Foundation));

    public TrashResult MoveToTrash(string path)
    {
        var full = Path.GetFullPath(path);
        FileTrash.RequireFile(full);
        // Whether a share keeps a Trash depends on its server; treating every network volume as having none means a file
        // never leaves the user's reach without the app's own permanent-delete question.
        if (StorageVolume.IsNetwork(Path.GetDirectoryName(full)!)) return TrashResult.Unavailable;
        _ = FoundationHandle.Value;
        var pool = PushAutoreleasePool();
        try
        {
            var manager = Send(Class("NSFileManager"), Selector("defaultManager"));
            var url = Send(Class("NSURL"), Selector("fileURLWithPath:"), String(full));
            if (SendTrash(manager, Selector("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, out var error) != 0)
                return TrashResult.Trashed;
            switch (SendInteger(error, Selector("code")))
            {
                case FeatureUnsupportedError: return TrashResult.Unavailable;
                case FileLockingError: return TrashResult.Locked;
            }
            var description = Marshal.PtrToStringUTF8(Send(Send(error, Selector("localizedDescription")), Selector("UTF8String")));
            throw new IOException($"macOS could not move {Path.GetFileName(full)} to the Trash. {description}");
        }
        finally { PopAutoreleasePool(pool); }
    }

    private static nint Class(string name) => GetClass(name) is not 0 and var handle ? handle
        : throw new PlatformNotSupportedException($"The Objective-C class {name} is not available.");

    private static nint String(string value) =>
        Send(Class("NSString"), Selector("stringWithUTF8String:"), System.Text.Encoding.UTF8.GetBytes(value + "\0"));

    [DllImport(ObjectiveC, EntryPoint = "objc_getClass", CharSet = CharSet.Ansi)]
    private static extern nint GetClass(string name);

    [DllImport(ObjectiveC, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
    private static extern nint Selector(string name);

    [DllImport(ObjectiveC, EntryPoint = "objc_autoreleasePoolPush")]
    private static extern nint PushAutoreleasePool();

    [DllImport(ObjectiveC, EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void PopAutoreleasePool(nint pool);

    // objc_msgSend must be called through a prototype matching each method; these are the ones used above.
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, byte[] utf8);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint SendInteger(nint receiver, nint selector);

    /// <summary>Returns the Objective-C BOOL, which is one byte on every Apple ABI.</summary>
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern byte SendTrash(nint receiver, nint selector, nint url, nint resultingUrl, out nint error);
}
