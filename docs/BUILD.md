# Сборка из исходников

## Windows

Windows x64, .NET 8 SDK. NuGet восстанавливает зависимости.

```powershell
dotnet publish windows/S8Cam.Receiver.csproj -c Release -r win-x64 --self-contained true -o dist/windows
dotnet run --project tests -c Release -- unit test-results
```

libusb-1.0.dll поставляется отдельно от EXE и может заменяться совместимой сборкой. Модули `windows/native/virtualcam/*.dll` MSBuild копирует в `tools/virtualcam` опубликованного клиента — сохраняйте эту папку при распространении. `dotnet publish` создаёт клиент, а полный Portable дополнительно включает FFmpeg/FFplay, ADB с DLL, APK и модель u2netp.onnx. Полная OBS в Portable не входит.

## Полный Portable

Сначала соберите Windows и подписанный Android release через `Build.ps1`. Для воспроизведения официальной комплектации используйте неизменённые пакеты по ссылкам и SHA256 в `packaging/portable-dependencies.json`: FFmpeg 9.0.1 full build, Platform Tools 37.0.1 и u2netp.onnx. Ссылка Google `latest` подвижная: если она уже отдаёт другую версию, проверка хэша остановит упаковку; не заменяйте закреплённые зависимости без проверки.

```powershell
.\Create-Portable-Package.ps1 -AdbDir C:\deps\platform-tools -FfmpegDir C:\deps\ffmpeg-9.0.1-full_build -ModelPath C:\deps\u2netp.onnx
```

Без этих параметров упаковщик ищет утилиты на машине сборки, но всё равно проверяет закреплённые хэши, обязательные DLL и upstream notices. Пропущенная DLL или несовместимая версия останавливают сборку. Копируются только выбранные публичные файлы; личные настройки, логи и ключи не нужны. Готовый файл: `dist/H3H-Cam-4.0.11-Portable.zip`.

Проверка Portable: `dotnet publish packaging/probe/ToolProbe.csproj -c Release -r win-x64 --self-contained true -o test-probe`. Скопируйте только `H3HCam Tool Probe.exe` рядом с клиентом в **тестовую** распаковку; запустите его с PATH из одной папки Windows System32 и пустыми настройками. Он использует исходный `ToolPaths.cs` клиента и проверяет, что инструменты найдены внутри `tools`, запускаются, декодируют H.264/HEVC и что присутствуют остальные компоненты. Сам тестовый EXE в релиз не включается.

## Android

Нужны JDK 17+, Android SDK API 37 и подходящий Build Tools. Точные версии Gradle/AGP заданы проектом. SDK указывается в ANDROID_HOME либо android/local.properties.

```powershell
cd android
.\gradlew.bat assembleDebug testDebugUnitTest lintDebug
```

Debug APK: android/app/build/outputs/apk/debug/app-debug.apk. Подпись debug отличается от официального релиза.

Для собственного release создайте свой keystore и скопируйте android/signing.properties.example в android/signing.properties. Заполните путь, alias и пароли. Оба приватных файла исключены из Git. Оригинальный ключ не публикуется.

```powershell
cd android
.\gradlew.bat assembleRelease
```

Без настроек подписи release будет неподписанным. Для обновления установленного APK нужна та же подпись.

## Публикация

Исходники — в main, бинарники — в Releases. Не коммитьте ключи, signing.properties, local.properties, AppData-настройки и личные логи. Публичный Portable создаётся Create-Portable-Package.ps1 и включает закреплённые FFmpeg/FFplay, ADB, модули виртуальной камеры и модель u2netp. Лицензии и upstream notices входят в ZIP; соответствующие исходники FFmpeg и остальных сторонних компонентов прикладываются к релизу. Полная OBS устанавливается отдельно.

Проверки производительности и аппаратного предпросмотра описаны в [PERFORMANCE.md](PERFORMANCE.md), нагрузочные проверки — в [GAME-LOAD.md](GAME-LOAD.md).

Проверка обновления NV12/GPU и ориентации OBS: [ANTIGRAVITY-REVIEW-4.0.8.md](ANTIGRAVITY-REVIEW-4.0.8.md). GPU-тест требует Windows/D3D11 и FFmpeg с libx264; аппаратные тесты дополнительно требуют подключённого телефона. Запускайте нагрузочные тесты и измерения производительности последовательно.

Исправление времени жизни USB-устройств и тесты запуска EXE: [STARTUP-CRASH-4.0.9.md](STARTUP-CRASH-4.0.9.md).

Установка виртуальной камеры и тест DirectShow-потребителя: [VIRTUAL-CAMERA-4.0.10.md](VIRTUAL-CAMERA-4.0.10.md). `dotnet run --project tests -c Release -- virtualcam-capture <путь-к-ffmpeg.exe> test-results/virtualcam` использует рабочую регистрацию камеры и требует остановить другие её производители. Обычные unit tests используют отдельные приватные ветки реестра.

QHD и GPU-путь: [QHD-4.0.11.md](QHD-4.0.11.md). Для инструментального теста подписанного APK выполните `gradlew.bat :app:assembleReleaseAndroidTest -PtestRelease=true`, установите release APK и `app/build/outputs/apk/androidTest/release/app-release-androidTest.apk` с одинаковой подписью, затем `adb shell am instrument -w -e class com.h3h.s8cam.CameraSurfaceScalerTest com.h3h.s8cam.test/androidx.test.runner.AndroidJUnitRunner`. Нужен свободный телефон с разрешением камеры. Тестовый APK не входит в Portable.
