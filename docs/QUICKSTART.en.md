# Quick start

[Русский](QUICKSTART.md) · **English**

## Install

Download the [full Portable 4.0.12](https://github.com/H3H-holylord/H3H-Cam/releases/download/v4.0.12/H3H-Cam-4.0.12-Portable.zip). Extract **all** files to a folder and run `H3HCam Receiver.exe`. Do not run it inside the ZIP viewer. Leave tool paths empty: .NET, FFmpeg/FFplay, ADB and the AI model are included.

Install `H3H-Cam-4.0.6.apk` on the phone and grant camera permission. You can transfer the APK and open it on Android, allowing installation from that source when prompted. For installation from Windows, enable developer options and USB debugging, connect a data cable, unlock the phone and run `Install-on-phone.cmd`. Confirm Android's debugging prompt. The installer requires exactly one authorized phone and updates the APK without deleting its data.

## Language

The language picker is at the top of both apps. Select **English**, **Русский** or **Automatic**. Automatic uses Russian when the system UI language is Russian, and English for other languages. The choices are saved independently on the PC and phone. Switching language keeps camera settings and the current stream. OEM/system errors and some technical logs may use their original language.

## USB

In Windows select **USB · via ADB**, press **FIND**, choose the camera and mode, then **START**. ADB reverse is configured automatically. If a device is `unauthorized`, unlock it and approve debugging. If it is missing, check USB debugging, the data cable and the manufacturer's Windows USB driver.

**USB Direct** is a separate option without Android debugging. It requires Android Accessory support and a WinUSB driver for the accessory device. This is not UVC support on every phone. Use ADB USB for the first test if available.

## Wi-Fi

Use the same trusted local network for the phone and PC. The PC can use Ethernet and the phone Wi-Fi through the same router. For initial setup, connect the authorized phone over USB; choose Wi-Fi and **FIND** so the client can enable wireless ADB and detect its address. Select the PC interface on that network. Firewall or router client isolation can prevent discovery.

## Camera and quality

Choose the camera, resolution, FPS and bitrate in **Windows**. The Android app follows these settings, so there are no matching video controls to adjust on the phone. Available modes depend on the device's Camera2 and codec capabilities. Use **All settings** for detailed controls.

For 2560×1440 / QHD, update both Windows and Android, press **FIND**, then select the mode. A **GPU from …** label means the phone downscales a larger supported capture size before encoding. Start with 30 FPS. If there is no suitable native or larger source, the mode is unavailable.

## OBS and video calls

Enable **Virtual camera** in H3H Cam and start streaming. In OBS add **Video Capture Device** and select the camera name shown in the client's status: **H3H Cam** or **OBS Virtual Camera**. No OBS plugin is needed for this path. Zoom, Discord and other video apps can select the same camera without installing OBS.

Virtual camera modules are included and registered for the current Windows user. If the video app was already open, refresh its camera list or restart it. Use **Install / repair camera** if registration reports an error. Do not run OBS's virtual camera output at the same time as H3H Cam's virtual camera output.

**Spout2** is an additional OBS output. Install a compatible [OBS Spout2 plugin](https://github.com/Off-World-Live/obs-spout2-plugin/releases), enable Spout2 in H3H Cam, add Spout2 Capture in OBS and select the H3HCam sender.

## Streaming during games

Disable previews and effects you do not need. Check detected FPS and dropped/stale frames. Limiting game FPS can leave more GPU capacity for capture and OBS. Increased process priority does not reserve GPU resources. You can turn off the phone screen using its power button while streaming; the black-screen button only dims the display.

For a bug report, include the Windows/client/APK versions, phone model, Android version, transport, resolution/FPS and symptoms. `Collect-Diagnostics.ps1` collects client diagnostics. Remove private IP addresses, serial numbers and paths before posting logs. Settings and logs are in `%LOCALAPPDATA%\H3HCam`. Safe startup: `H3HCam Receiver.exe --safe-mode`.
