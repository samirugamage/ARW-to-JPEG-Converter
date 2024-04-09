# ARW to JPEG Conversion Script
This script automates the conversion of ARW (Sony RAW) image files to JPEG format, preserving the original image's metadata. It processes files in the current directory and its subdirectories, using ImageMagick and ExifTool to handle the conversion and metadata transfer, respectively.

## What It Does

1. Finds ARW Files :Scans the current directory and all subdirectories for ARW files.
2. Converts to JPEG: Converts each found ARW file into a JPEG file with 100% quality, preserving the highest possible image fidelity.
3. Transfers Metadata: Copies all metadata from the original ARW file to the newly created JPEG file, ensuring that information like camera settings, date taken, and GPS data is not lost in the conversion process.

## How It Works
The script utilizes a batch processing approach, leveraging Windows Batch File (.bat) scripting capabilities. It employs two external tools: ImageMagick for image conversion and ExifTool for metadata handling. The process involves iterating over each ARW file, converting it to JPEG, and then applying the metadata from the original file to the JPEG.

## Prerequisites
To use this script, you need:

1. Windows OS: The script is a .bat file, designed to run on Windows.
2. [ImageMagick](https://imagemagick.org/script/download.php): This free software suite is used for converting images between formats. It must be installed and accessible from the command line.
3. [ExifTool](https://exiftool.org/): A powerful tool to read, write, and edit meta information in a wide variety of files. It is required for transferring metadata.
Ensure that you have both tools installed before running the script. You should also set the exifToolPath variable in the script to the location where ExifTool is installed on your system.

## Installation

1. Install ImageMagick: Download and install from ImageMagick's official website.
2. Install ExifTool: Download and install from ExifTool's official website.
3. Rename the exiftool_versionNo.exe to exiftool.exe
4. Configure Script: Edit the script to set the exifToolPath variable to the path where ExifTool is installed on your system.

   
### How to Execute
1. Save the script as a .bat file format on your computer.
2. Navigate to the directory where you saved the script.
3. Copy the Script and paste it on a folder that contains ARW files.
4. Open the script by clicking on it.
5. The script will start processing the ARW files, converting them to JPEG format, and transferring the metadata.
## Note

> The script pauses at the end of execution. Press any key to close the Command Prompt window after the conversion is complete.
Ensure that the paths to ImageMagick and ExifTool are correctly set and accessible from the command line for the script to function properly.
License

### MIT License - Feel free to use and modify this script as needed.
