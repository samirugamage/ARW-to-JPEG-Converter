@echo off
SETLOCAL

:: Converts every ARW in this folder and its subfolders to a JPEG next to it,
:: using ImageMagick, then copies all metadata with ExifTool.
:: Photos that already have a JPEG are skipped.

:: ---------------- settings ----------------
:: ExifTool: "exiftool" works when it is installed (winget install OliverBetz.ExifTool).
:: Otherwise put the full path here, e.g. C:\Tools\exiftool.exe
SET "EXIFTOOL=exiftool"
:: JPEG quality. 88-92 looks the same as 100 on photos but is several times smaller.
:: Quality 100 is why some JPEGs used to come out bigger than the ARW.
SET "QUALITY=90"
:: ------------------------------------------

echo Converting ARW files in "%CD%" and subfolders...

:: -define dng:use-camera-wb=true  use the camera's white balance (must come BEFORE the input file)
:: -strip                          drop ImageMagick's own profiles; exiftool adds the real metadata below
:: -sampling-factor 4:2:0          standard chroma subsampling (ImageMagick skips it above quality 90)
:: -interlace JPEG                 progressive JPEG, usually a little smaller
:: -define jpeg:optimize-coding    optimized Huffman tables, smaller at zero quality cost
FOR /R %%G IN (*.ARW) DO (
    IF EXIST "%%~dpnG.jpg" (
        echo   skip %%~nxG ^(JPEG already exists^)
    ) ELSE (
        echo   %%~nxG
        magick -define dng:use-camera-wb=true "%%G" -strip -sampling-factor 4:2:0 -interlace JPEG -define jpeg:optimize-coding=true -quality %QUALITY% "%%~dpnG.jpg"
        "%EXIFTOOL%" -q -tagsFromFile "%%G" -all:all -overwrite_original "%%~dpnG.jpg"
    )
)

echo.
echo Conversion complete.

ENDLOCAL
pause
