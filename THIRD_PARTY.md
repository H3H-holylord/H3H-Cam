# Сторонние компоненты

H3H Cam — GPL-3.0. Сторонние компоненты сохраняют собственные лицензии.

- libusb 1.0.30: LGPL-2.1-or-later. Отдельная DLL без модификаций, заменяемая пользователем. [Исходники и сборка](https://github.com/libusb/libusb/tree/v1.0.30), [релиз](https://github.com/libusb/libusb/releases/tag/v1.0.30). Лицензия в licenses/libusb-LGPL-2.1.txt.
- LibUsbDotNet 3.0.224: LGPL-3.0-or-later. [Проект](https://github.com/LibUsbDotNet/LibUsbDotNet). Точная ревизия a89bc81569327840e98a2c30207e5b74bbc97b3a указана NuGet; её исходники приложены к релизу в ThirdParty-Sources.zip. Текст в licenses/LibUsbDotNet-LICENSE.txt. Приложение можно перепубликовать с изменённой зависимостью.
- ONNX Runtime DirectML 1.24.4: MIT и лицензии зависимостей. [Исходники](https://github.com/microsoft/onnxruntime). LICENSE и ThirdPartyNotices из NuGet включены в licenses/.
- .NET/WPF: [runtime](https://github.com/dotnet/runtime), [WPF](https://github.com/dotnet/wpf). Gradle wrapper: Apache-2.0, [Gradle](https://github.com/gradle/gradle). AndroidX/Material восстанавливаются Gradle.
- FFmpeg/FFplay, Android Platform Tools и полная OBS Studio устанавливаются отдельно. Ссылки в docs/QUICKSTART.md. Весовые файлы AI не включены.
- OBS Virtual Camera 32.2.2: неизменённые официальные DLL x64/x86 входят в `tools/virtualcam` Windows ZIP, GPL-2.0-or-later. Лицензия в `licenses/OBS-GPL-2.0.txt`, происхождение/хэши и сборка в [windows/native/virtualcam/README.md](windows/native/virtualcam/README.md). Полные соответствующие исходники с libdshowcapture, `OBS-Studio-32.2.2-Sources.tar.gz`, включены в `ThirdParty-Sources.zip` этого релиза. [Официальный релиз и исходники](https://github.com/obsproject/obs-studio/releases/tag/32.2.2).
- Обмен с OBS Virtual Camera использует совместимый shared-memory ABI. [Протокол OBS](https://github.com/obsproject/obs-studio/tree/32.2.2/shared/obs-shared-memory-queue), GPL-2.0-or-later.
