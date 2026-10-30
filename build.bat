@echo off
rem Builds a single self-contained LyricsPlayer.exe into the "publish" folder.
rem Requires the .NET SDK (see: dotnet --version).

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
if errorlevel 1 (
    echo.
    echo BUILD FAILED. See the messages above.
    pause
    exit /b 1
)

echo.
echo Done: publish\LyricsPlayer.exe
echo Put your mp3/flac/wav files next to it and run it.
pause
