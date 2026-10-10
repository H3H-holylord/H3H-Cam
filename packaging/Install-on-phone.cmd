@echo off
chcp 65001 >nul
setlocal
title H3H Cam - install on phone
if not exist "%~dp0tools\adb.exe" goto incomplete
if not exist "%~dp0H3H-Cam-4.0.6.apk" goto incomplete
echo Enable USB debugging on your phone and connect a data cable.
echo Unlock it and approve the debugging prompt if it appears.
echo This installs H3H Cam on ONE connected and authorized phone.
echo An existing installation is updated without deleting its data.
echo.
pause
pushd "%~dp0tools" || exit /b 1
adb.exe start-server
if errorlevel 1 goto failed
set "H3HCAM_COUNT=0"
set "H3HCAM_PHONE="
for /f "tokens=1,2" %%A in ('adb.exe devices') do if "%%B"=="device" (
    set /a H3HCAM_COUNT+=1 >nul
    set "H3HCAM_PHONE=%%A"
)
if not "%H3HCAM_COUNT%"=="1" goto no_single_phone
adb.exe -s "%H3HCAM_PHONE%" install -r "%~dp0H3H-Cam-4.0.6.apk"
if errorlevel 1 goto failed
popd
echo.
echo Done. Open H3H Cam on your phone and grant camera permission.
echo In Windows select USB via ADB, press FIND, then START.
pause
exit /b 0
:no_single_phone
echo.
adb.exe devices
echo Exactly one authorized phone is required. Disconnect other devices.
echo If unauthorized, unlock the phone and approve USB debugging.
echo If the list is empty, check the cable, debugging and phone USB driver.
popd
pause
exit /b 1
:failed
popd
echo.
echo Installation failed. Check the message above and phone access.
echo Uninstalling an app with a different signature deletes its settings;
echo this installer does not automatically uninstall anything.
pause
exit /b 1
:incomplete
echo Extract the ENTIRE Portable archive, including tools and the APK.
pause
exit /b 1
