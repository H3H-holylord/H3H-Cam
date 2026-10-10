@echo off
chcp 65001 >nul
setlocal
title H3H Cam - установка на телефон
if not exist "%~dp0tools\adb.exe" goto incomplete
if not exist "%~dp0H3H-Cam-4.0.4.apk" goto incomplete
echo Включите на телефоне «Отладка по USB» и подключите кабель для данных.
echo Разблокируйте телефон и подтвердите разрешение отладки, если оно появится.
echo Этот файл установит H3H Cam на ОДИН подключённый и разрешённый телефон.
echo Если H3H Cam уже установлена, будет выполнено обновление с сохранением данных.
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
adb.exe -s "%H3HCAM_PHONE%" install -r "%~dp0H3H-Cam-4.0.4.apk"
if errorlevel 1 goto failed
popd
echo.
echo Готово. Откройте H3H Cam на телефоне и разрешите доступ к камере.
echo В Windows выберите «USB · через ADB», нажмите НАЙТИ, затем СТАРТ.
pause
exit /b 0
:no_single_phone
echo.
adb.exe devices
echo Не найден один разрешённый телефон. Оставьте подключённым только нужный.
echo При unauthorized разблокируйте телефон и подтвердите разрешение отладки.
echo Если список пуст: проверьте кабель, отладку и USB-драйвер производителя.
popd
pause
exit /b 1
:failed
popd
echo.
echo Установка не завершена. Проверьте сообщение выше и доступ к телефону.
echo При конфликте подписи удаление приложения удалит его настройки;
echo этот установщик ничего автоматически не удаляет.
pause
exit /b 1
:incomplete
echo Распакуйте ВЕСЬ Portable-архив вместе с папкой tools и APK.
pause
exit /b 1
