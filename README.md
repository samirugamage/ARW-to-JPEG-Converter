ARW to JPEG Converter (Windows GUI)

This Windows application converts Sony RAW (.ARW) images to JPEG while preserving all metadata from the original files.
It uses ImageMagick for image conversion and ExifTool for metadata transfer.
Features

    Batch Conversion – Process entire folders of .ARW files at once, with optional subfolder scanning.

    JPEG Quality Control – Set output quality (default 100%).

    Metadata Preservation – Camera settings, dates, GPS data, and other EXIF info are copied to the JPEG.

    Custom Tool Paths – Easily set and save the paths to magick.exe and exiftool.exe.

    Progress & Logs – View live conversion progress and detailed logs for each file.

How It Works

    The app scans the chosen folder (and optionally subfolders) for .ARW files.

    Each file is converted to JPEG with the selected quality using ImageMagick.

    All metadata from the original .ARW is copied to the JPEG using ExifTool.

    Conversion status is displayed in the log panel.

Prerequisites

You need two external tools:

    ImageMagick – Download here

    ExifTool – Download here (rename exiftool(-version).exe to exiftool.exe)

    You can place both magick.exe and exiftool.exe inside a tools folder next to the ArwToJpegGui.exe file for automatic detection.

Installation

    Download the latest ArwToJpegGui.zip artifact from the GitHub Actions page (or Releases if available).

    Extract the .zip file.

    Place magick.exe and exiftool.exe in a folder named tools next to ArwToJpegGui.exe.

    First run the app and set the paths to these tools if not auto-detected. Click Save paths to store them.

Usage

    Launch ArwToJpegGui.exe.

    Select the folder containing .ARW files.

    Choose whether to include subfolders.

    Set JPEG quality (default is 100).

    Click Start.

    Monitor progress and logs in the app.

Notes

    The app stores your last used settings in settings.json next to the .exe.

    SmartScreen may warn on first run — click “More info” → “Run anyway”.

    Artifacts from GitHub Actions expire after a set period. For a permanent download, use a Release build if provided.

License

MIT License — Free to use and modify.
