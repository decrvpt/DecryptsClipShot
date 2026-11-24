# Third-party notices

Decrypt's ClipShot uses the `FortniteReplayReader` NuGet package to analyze local Fortnite replay files. The package and its source project are distributed under the MIT License.

- Project: https://github.com/Shiqan/FortniteReplayDecompressor
- Package: https://www.nuget.org/packages/FortniteReplayReader

Copyright notices and the complete package license are available from those locations and are retained by the package during restore and publication.

## NAudio

Decrypt's ClipShot uses the `NAudio.Wasapi` and `NAudio.Core` NuGet packages for Windows WASAPI playback-loopback capture. NAudio is distributed under the MIT License.

- Project: https://github.com/naudio/NAudio
- Package: https://www.nuget.org/packages/NAudio.Wasapi

## FFmpeg

The installer includes `ffmpeg.exe`, used to record and save clips. The bundled build comes from BtbN/FFmpeg-Builds (GPL configuration, which includes x264) and is licensed under the GNU General Public License v3.

- FFmpeg source code: https://ffmpeg.org/download.html
- Build scripts and release used: https://github.com/BtbN/FFmpeg-Builds
- Licence details: https://ffmpeg.org/legal.html

## WiX Toolset

The MSI installer is built with the WiX Toolset (Microsoft Reciprocal License). Building it requires accepting the WiX Open Source Maintenance Fee EULA: https://docs.firegiant.com/wix/osmf/
