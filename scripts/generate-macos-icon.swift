// Run on macOS: swift scripts/generate-macos-icon.swift source.png output.icns
import AppKit

guard CommandLine.arguments.count == 3 else {
    fatalError("Usage: swift generate-macos-icon.swift source.png output.icns")
}
let source = URL(fileURLWithPath: CommandLine.arguments[1])
let output = URL(fileURLWithPath: CommandLine.arguments[2])
guard let image = NSImage(contentsOf: source) else { fatalError("Cannot read source icon") }
var chunks = Data()
func lengthBytes(_ value: Int) -> Data {
    var bigEndian = UInt32(value).bigEndian
    return withUnsafeBytes(of: &bigEndian) { Data($0) }
}
let types = [16: ["icp4", "ic11"], 32: ["icp5", "ic12"],
             128: ["ic07", "ic13"], 256: ["ic08", "ic14"],
             512: ["ic09", "ic10"]]

// macOS requires square representations. Transparent padding keeps the original
// portrait artwork intact, without cropping or stretching the VHS spines.
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let pixels = points * scale
        let side = CGFloat(pixels)
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels,
            pixelsHigh: pixels, bitsPerSample: 8, samplesPerPixel: 4,
            hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
            bytesPerRow: 0, bitsPerPixel: 0)!
        let context = NSGraphicsContext(bitmapImageRep: bitmap)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        context.imageInterpolation = .high
        NSColor.clear.setFill()
        NSRect(x: 0, y: 0, width: side, height: side).fill(using: .copy)
        let ratio = min(side / image.size.width, side / image.size.height)
        let width = image.size.width * ratio
        let height = image.size.height * ratio
        image.draw(in: NSRect(x: (side - width) / 2, y: (side - height) / 2,
                             width: width, height: height),
                   from: .zero, operation: .sourceOver, fraction: 1)
        NSGraphicsContext.restoreGraphicsState()
        let png = bitmap.representation(using: .png, properties: [:])!
        chunks.append(Data(types[points]![scale - 1].utf8))
        chunks.append(lengthBytes(png.count + 8))
        chunks.append(png)
    }
}
// ICNS stores PNG representations in typed, big-endian length-prefixed chunks.
var container = Data("icns".utf8)
container.append(lengthBytes(chunks.count + 8))
container.append(chunks)
try container.write(to: output, options: .atomic)
