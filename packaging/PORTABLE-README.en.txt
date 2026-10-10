H3H Cam 4.0.12 - full Portable for Windows x64

1. Extract the ENTIRE ZIP into a folder. Do not run the EXE inside the ZIP.
2. Run H3HCam Receiver.exe. .NET, FFmpeg/FFplay and ADB are included.
3. Install H3H-Cam-4.0.6.apk on your Android phone and allow camera access.
   Transfer the APK and open it on Android, or enable USB debugging,
   connect one authorized phone and run Install-on-phone.cmd.
4. Select "USB - via ADB" in Windows, press FIND, choose a camera and START.
   Leave tool paths empty. Some phones need a manufacturer's USB driver.

Choose English, Русский or Automatic at the top of either app.
Automatic uses Russian on a Russian system and English otherwise.
Each app remembers its language independently. Switching language keeps
camera settings and the stream. Read docs/QUICKSTART.en.md for more help.

Enable Virtual camera for OBS, Zoom, Discord and other video apps.
Select the camera name shown in the client's status: H3H Cam or OBS Virtual Camera.
In OBS add a Video Capture Device source. Restart an already open video app
if the camera is missing. OBS itself is not required for video calls.
Do not run OBS's virtual camera output at the same time as H3H Cam's output.

Wi-Fi: use the same trusted local network. Ethernet on the PC is supported.
Connect an authorized USB phone for the initial wireless ADB setup.
USB Direct is separate: it requires Accessory support and a WinUSB driver.
For the first test, USB via ADB is recommended.

2560x1440 / QHD can use native capture or GPU downscaling on the phone.
Available resolution and 60 FPS depend on Camera2, codecs and firmware.
Turn off unused preview windows and heavy effects to reduce game-time load.

Keep tools and models next to the EXE. The package includes .NET, FFmpeg,
FFplay, ADB with DLLs, libusb, virtual camera x64/x86, APK and an AI model.
OBS, video-call apps and device drivers are installed separately.
Spout2 output requires an OBS plugin. Phone audio is not transmitted.

Settings/logs: %LOCALAPPDATA%\H3HCam
Diagnostics: Collect-Diagnostics.ps1
Safe startup: H3HCam Receiver.exe --safe-mode
Issues: https://github.com/H3H-holylord/H3H-Cam/issues
Licenses: LICENSE, THIRD_PARTY.md, licenses. Integrity: SHA256SUMS.txt.
