@echo off
SETLOCAL

:: Set the path to your exiftool.exe here
:: Change to your username
SET "exifToolPath=C:\Users\username\Downloads\exiftool-12.80\exiftool.exe"

echo Processing ARW files in the current directory and subdirectories...

:: Convert ARW files to JPEG with 100% quality in current directory and subdirectories
FOR /R %%G IN (*.ARW) DO (
    echo Converting "%%G"...
    magick "%%G" -quality 100 "%%~dpnG.jpg"
    echo Embedding metadata from "%%G" to "%%~nG.jpg"...
    "%exifToolPath%" -TagsFromFile "%%G" -all:all -overwrite_original "%%~dpnG.jpg"
)

echo Conversion complete.

ENDLOCAL
pause
