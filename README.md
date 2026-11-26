# ARW to JPEG Converter (Windows GUI)

This Windows application converts Sony RAW (.ARW) images to space-efficient JPEGs while preserving all metadata from the original files.
It can use either **RawTherapee** (best look) or **ImageMagick** (fastest) for the conversion, and ExifTool for metadata transfer.

## Features

- **Two engines**
  - **RawTherapee** (default when installed): full RAW development with automatic lens correction, highlight recovery and a tone curve that matches the camera's own JPEG. A noise profile is picked per photo from its ISO.
  - **ImageMagick**: plain RAW decode. About 3x faster, flatter and darker look.
- **Small files, same look**: JPEGs are written at quality 90 with 4:2:0 chroma subsampling (the standard for photos).
- **Batch conversion**: process entire folders of .ARW files at once, with optional subfolder scanning.
- **Parallel jobs**: converts several files at once (default: half your CPU cores, max 4).
- **Metadata preservation**: camera settings, dates, GPS data, lens info, maker notes and other EXIF info are copied from each ARW in one fast ExifTool run.
- **Size report**: the log shows ARW size -> JPEG size per file, the total saved, and warns if a JPEG came out larger than its ARW.
- **Safe output**: each JPEG is written under a temporary name and renamed when complete, so a cancel never leaves a half-written file.
- **Automatic tool detection**: finds the tools in a `tools` folder next to the app, on PATH, or (for RawTherapee) in `Program Files\RawTherapee\<version>`.

## Test results (Sony A6400, 7 photos at ISO 100, 8-core PC)

| Method | Total size | Time |
|---|---|---|
| Original ARW files | 166.6 MB | - |
| Old version (ImageMagick, quality 100) | 118.3 MB | ~70 s |
| **ImageMagick engine** (quality 90) | 14.3 MB | 30 s |
| **RawTherapee engine** (quality 90) | 20.9 MB | ~110 s |

RawTherapee files are a little bigger because its sharpening and lens correction keep more fine detail.

## RawTherapee noise profiles

The app uses RawTherapee's bundled "Auto-Matched Curve" profiles and picks one per photo:

| Photo ISO | Profile | What it does |
|---|---|---|
| up to 800 | ISO Low | capture sharpening, no noise reduction |
| 801 - 3200 | ISO Medium | colour noise reduction |
| above 3200 | ISO High | stronger luminance + colour noise reduction |

Noise reduction also makes high-ISO JPEGs much smaller, because noise is expensive to store.

## Why the JPEGs are smaller now

| Setting | Effect |
|---|---|
| Quality 90 instead of 100 | Quality 100 barely quantizes anything, so sensor noise is stored almost verbatim. That is why some JPEGs were bigger than the ARW. 88-92 looks the same on photos. |
| 4:2:0 chroma subsampling | ImageMagick turns it off above quality 90. 4:2:0 is the standard for photos (cameras use it too). |
| Progressive + optimized Huffman (ImageMagick) | A few percent smaller with zero quality loss. |
| Camera white balance (ImageMagick) | `-define dng:use-camera-wb=true` uses the white balance you shot with. |

## Prerequisites

Install with winget (or download from the websites):

```
winget install RawTherapee.RawTherapee
winget install ImageMagick.ImageMagick
winget install OliverBetz.ExifTool
```

- **ExifTool** is always required (metadata).
- **RawTherapee** is required for the RawTherapee engine: https://rawtherapee.com
- **ImageMagick** is required for the ImageMagick engine: https://imagemagick.org

To build from source you also need the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`), then run `dotnet build -c Release`.

## Usage

1. Launch `ArwToJpegGui.exe`.
2. Select the folder containing .ARW files.
3. Pick the engine, whether to include subfolders, the JPEG quality (default 90) and the number of parallel jobs.
4. Click **Start**.
5. Check the log for sizes. If any file is flagged as larger than its ARW, lower the quality (e.g. 85).

Keep your ARW files until you have checked a few JPEGs at 100% zoom and confirmed the quality is what you want.

## Command-line version

`arw-to-jpeg.bat` does the ImageMagick conversion with no GUI: put it in the folder with your ARW files, set `EXIFTOOL` and `QUALITY` at the top, and run it.

## Notes

- Settings are stored in `settings.json` next to the .exe. Settings saved by older versions with quality 100 are moved to 90 once.
- RawTherapee already uses all CPU cores for one photo, so more parallel jobs mostly helps ImageMagick.
- Each parallel job can use 1-1.5 GB of RAM on 24 MP files. Lower the job count if your PC runs out of memory.
- With RawTherapee the JPEG pixels are already rotated upright, so the ARW's rotation flag is not copied. With ImageMagick the pixels stay as shot and the rotation flag is copied.
- SmartScreen may warn on first run: click "More info" -> "Run anyway".

## License

MIT License. Free to use and modify.
