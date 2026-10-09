# Сборка из исходников

## Windows

Windows x64, .NET 8 SDK. NuGet восстанавливает зависимости.

```powershell
dotnet publish windows/S8Cam.Receiver.csproj -c Release -r win-x64 --self-contained true -o dist/windows
dotnet run --project tests -c Release -- unit test-results
```

libusb-1.0.dll поставляется отдельно от EXE и может заменяться совместимой сборкой. FFmpeg, ADB и OBS устанавливаются отдельно. Для необязательных AI-эффектов поместите совместимую u2netp.onnx в models рядом с EXE.

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

Исходники — в main, бинарники — в Releases. Не коммитьте ключи, signing.properties, local.properties, AppData-настройки и личные логи. Локальный Create-Portable-Package.ps1 предназначен для личной полной сборки; публичный ZIP не включает FFmpeg/ADB/OBS и веса AI.

Проверки производительности и аппаратного предпросмотра описаны в [PERFORMANCE.md](PERFORMANCE.md), нагрузочные проверки — в [GAME-LOAD.md](GAME-LOAD.md).

Проверка обновления NV12/GPU и ориентации OBS: [ANTIGRAVITY-REVIEW-4.0.8.md](ANTIGRAVITY-REVIEW-4.0.8.md). GPU-тест требует Windows/D3D11 и FFmpeg с libx264; аппаратные тесты дополнительно требуют подключённого телефона. Запускайте нагрузочные тесты и измерения производительности последовательно.
