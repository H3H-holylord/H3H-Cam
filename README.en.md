![H3H Cam](docs/banner.svg)

# Your Android phone as a Windows camera

[Русский](README.md) · **English**

**H3H Cam** connects an Android phone to Windows for streaming, video calls and recording. Select the camera and video settings on your computer; the phone captures and encodes video with its hardware codec.

[**Download the full Portable 4.0.12**](https://github.com/H3H-holylord/H3H-Cam/releases/download/v4.0.12/H3H-Cam-4.0.12-Portable.zip) · [Quick start](docs/QUICKSTART.en.md) · [Report a problem](https://github.com/H3H-holylord/H3H-Cam/issues)

## Start here

1. Extract the **entire** Portable ZIP to a folder and run `H3HCam Receiver.exe`. Keep `tools` and `models` next to the EXE. .NET, FFmpeg, FFplay and ADB are included; leave tool paths empty.
2. Install `H3H-Cam-4.0.6.apk` on your phone, open it and allow camera access. You can transfer the APK to the phone and open it there, or enable USB debugging and use `Install-on-phone.cmd` with one authorized phone connected.
3. In Windows select **USB · via ADB**, press **FIND**, choose a camera, then **START**. Confirm the debugging prompt on the phone. Some phones need their manufacturer's Windows USB driver.
4. Enable **Virtual camera**. In OBS add a **Video Capture Device** source and choose the camera name shown in H3H Cam's status. Zoom and Discord can use the same camera. OBS itself is not required for video calls.

Select **English**, **Русский** or **Automatic** from the language picker at the top of either app. Automatic uses Russian on a Russian system and English otherwise. Each device remembers its choice; changing it does not restart the stream.

## Features

- Hardware H.264 / HEVC encoding, adjustable resolution, bitrate and 30/60 FPS where supported. 1080p60 has been tested on Samsung Note9; availability depends on Camera2 and the phone firmware.
- 2560×1440 / QHD: native capture or GPU downscaling from a larger supported 16:9 source on the phone. The client shows the actual capture source and available FPS; this does not upscale 1080p to QHD.
- USB via ADB reverse, Wi-Fi over RTP/UDP and USB Direct/AOA with a compatible phone and WinUSB driver. USB Direct is not a universal USB UVC device.
- Camera module selection, focus, exposure, white balance, zoom, stabilization and automatic framing where available.
- Optional preview, virtual camera output and Spout2 output for OBS. Spout2 requires a separate OBS plugin.
- Background foreground service, screen off while streaming, reconnection, FPS/bitrate and battery statistics.
- Shared decoder for Spout2 and preview, bounded frame queues and stale-frame dropping to reduce delay under load.

For games, turn off unused previews and effects and leave some GPU capacity available. Camera delay also depends on capture, decoding, transport and game load. Not every phone exposes every resolution or 60 FPS through Camera2. Phone audio is not transmitted; use a separate microphone. Wi-Fi and ADB control are intended for a trusted local network.

## Downloads and development

The full Portable includes the Windows client with .NET, Android APK, FFmpeg/FFplay, ADB with its DLLs, libusb, x64/x86 virtual camera modules, an AI model and instructions. OBS, video-call apps and device drivers are installed separately.

[Android APK 4.0.6](https://github.com/H3H-holylord/H3H-Cam/releases/download/v4.0.12/H3H-Cam-4.0.6.apk) · [Release checksums](https://github.com/H3H-holylord/H3H-Cam/releases/download/v4.0.12/SHA256SUMS.txt)

Source: `android/` for capture/encoding, `windows/` for .NET 8 WPF and output, `tests/` for verification, `docs/` for instructions. On Windows with .NET 8 SDK, run `dotnet publish windows/S8Cam.Receiver.csproj -c Release -r win-x64 --self-contained true` and `dotnet run --project tests -c Release -- unit test-results`. Android needs JDK 17+ and Android SDK API 37: run `gradlew.bat assembleDebug testDebugUnitTest lintDebug` from `android/`. [Detailed build instructions (Russian)](docs/BUILD.md).

**GPL-3.0.** Third-party components retain their own licenses: [THIRD_PARTY.md](THIRD_PARTY.md). Signing keys and personal settings are excluded from source exports. Windows stores settings and logs in `%LOCALAPPDATA%\H3HCam`.

Created by [H3H / HolyLord](https://github.com/H3H-holylord).
