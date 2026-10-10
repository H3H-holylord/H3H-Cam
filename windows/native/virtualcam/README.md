# OBS DirectShow camera modules

Unmodified `obs-virtualcam-module64.dll` and `obs-virtualcam-module32.dll` from the official [OBS Studio 32.2.2 Windows x64 ZIP](https://github.com/obsproject/obs-studio/releases/tag/32.2.2). Both were compared byte for byte with that download on 2026-10-10. They implement the OBS Virtual Camera shared-memory protocol; H3H Cam supplies NV12 frames.

| Module | SHA256 |
|---|---|
| 64-bit | `0dcde4a969a7ce45a39472d39bd15f5bfff242a06449b27c61e20e667358ec34` |
| 32-bit | `e9513840e2b96db8ceeb41cf6f5fdb85e58fe7dc44b6b9afb62b198c5505aa88` |

License: GPL-2.0-or-later, see `licenses/OBS-GPL-2.0.txt`. Corresponding source including libdshowcapture is supplied as `OBS-Studio-32.2.2-Sources.tar.gz` inside the release's `ThirdParty-Sources.zip`. It is the official source archive from the same OBS release.

For rebuilding, follow the OBS source archive's Windows CMake build instructions and presets, enable `ENABLE_VIRTUALCAM` with `VIRTUALCAM_GUID=A3FCE0F5-3493-419F-958A-ABA1250EC20B`, and build the `obs-virtualcam-module` target for x64 and Win32. Its CMake scripts include the shared-memory queue, tiny NV12 scaler, threading helpers and libdshowcapture. The module uses a static MSVC runtime.

MSBuild copies these modules into `tools/virtualcam` alongside the published EXE. Registration belongs to the current user, with a persistent DLL copy under H3H Cam's AppData directory. A working existing OBS registration is reused. OBS and H3H Cam must not produce into `OBSVirtualCamVideo` simultaneously.
