param(
    [string]$DistDir = '',
    [string]$AdbDir = '',
    [string]$FfmpegDir = '',
    [string]$ModelPath = '',
    [switch]$NoZip
)
$ErrorActionPreference = 'Stop'
$s8Root = $PSScriptRoot
if (-not $DistDir) { $DistDir = Join-Path $s8Root 'dist' }
$DistDir = [IO.Path]::GetFullPath($DistDir)
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$dependencyManifest = Get-Content -LiteralPath (Join-Path $s8Root 'packaging\portable-dependencies.json') -Raw | ConvertFrom-Json

function Assert-Dependency([string]$Path, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Portable dependency missing: $Path" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) {
        throw "Portable dependency hash mismatch: $Path. Use the versions in packaging/portable-dependencies.json."
    }
}

Write-Host "=== Создание автономного Portable-пакета H3H Cam 4.0.12 ===" -ForegroundColor Cyan

function Get-AndroidTool([string]$ToolName) {
    $sdk = $env:ANDROID_HOME
    if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
    $found = Get-ChildItem -Path (Join-Path $sdk 'build-tools') -Filter $ToolName -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.FullName }
    $cmd = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Test-ApkIsRelease([string]$ApkPath) {
    if (-not (Test-Path $ApkPath)) { return $false }
    $aapt = Get-AndroidTool 'aapt.exe'
    if (-not $aapt) { throw 'aapt.exe is required to verify a release APK' }
    if ($aapt) {
        $badging = & $aapt dump badging $ApkPath
        if ($LASTEXITCODE -ne 0) { throw "APK metadata verification failed: $ApkPath" }
        $badgingStr = $badging -join "`n"
        if ($badgingStr -match "application-debuggable") { return $false }
        if (-not ($badgingStr -match "package:\s+name='com\.h3h\.s8cam'")) { return $false }
        if (-not ($badgingStr -match "versionCode='19'")) { return $false }
        if (-not ($badgingStr -match "versionName='4\.0\.6'")) { return $false }
    }
    $apksigner = Get-AndroidTool 'apksigner.bat'
    if (-not $apksigner) { throw 'apksigner.bat is required to verify a release APK' }
    if ($apksigner) {
        if (-not $env:JAVA_HOME) {
            $s8Jbr = Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'
            if (Test-Path $s8Jbr) { $env:JAVA_HOME = $s8Jbr }
        }
        $certs = & $apksigner verify --verbose --print-certs $ApkPath
        if ($LASTEXITCODE -ne 0) { throw "APK signature verification failed: $ApkPath" }
        $certsStr = $certs -join "`n"
        if ($certsStr -match "CN=Android Debug") { return $false }
    }
    return $true
}

function Assert-ReleaseApk([string]$ApkPath) {
    if (-not (Test-Path $ApkPath)) { throw "Release APK not found: $ApkPath" }

    $aapt = Get-AndroidTool 'aapt.exe'
    if (-not $aapt) { throw 'aapt.exe is required to verify a release APK' }
    if ($aapt) {
        $badging = & $aapt dump badging $ApkPath
        if ($LASTEXITCODE -ne 0) { throw "APK metadata verification failed: $ApkPath" }
        $badgingStr = $badging -join "`n"
        if ($badgingStr -match "application-debuggable") {
            throw "RELEASE GUARD FAILED: APK is marked as debuggable! ($ApkPath)"
        }
        if (-not ($badgingStr -match "package:\s+name='com\.h3h\.s8cam'")) {
            throw "RELEASE GUARD FAILED: Package name must be com.h3h.s8cam"
        }
        if (-not ($badgingStr -match "versionCode='19'")) {
            throw "RELEASE GUARD FAILED: versionCode must be 19"
        }
        if (-not ($badgingStr -match "versionName='4\.0\.6'")) {
            throw "RELEASE GUARD FAILED: versionName must be 4.0.6"
        }
    }

    $apksigner = Get-AndroidTool 'apksigner.bat'
    if (-not $apksigner) { throw 'apksigner.bat is required to verify a release APK' }
    if ($apksigner) {
        if (-not $env:JAVA_HOME) {
            $s8Jbr = Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'
            if (Test-Path $s8Jbr) { $env:JAVA_HOME = $s8Jbr }
        }
        $certs = & $apksigner verify --verbose --print-certs $ApkPath
        if ($LASTEXITCODE -ne 0) { throw "APK signature verification failed: $ApkPath" }
        $certsStr = $certs -join "`n"
        if ($certsStr -match "CN=Android Debug") {
            throw "RELEASE GUARD FAILED: APK is signed with Android Debug certificate! ($ApkPath)"
        }
        if (-not ($certsStr -match "CN=H3HCam")) {
            Write-Warning "APK signer certificate is not CN=H3HCam: $certsStr"
        }
    }
    Write-Host "✅ APK Release Guard: $ApkPath подтверждён (release, non-debuggable, release signer)." -ForegroundColor Green
}

function Assert-ReleaseExe([string]$ExePath) {
    if (-not (Test-Path $ExePath)) { throw "Receiver EXE does not exist: $ExePath" }
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
    if ($info.FileVersion -ne '4.0.12.0') {
        throw "RELEASE GUARD FAILED: Receiver FileVersion must be 4.0.12.0 (got: $($info.FileVersion))"
    }
    Write-Host "✅ Receiver Release Guard: $ExePath подтверждён (Version: $($info.FileVersion))." -ForegroundColor Green
}

# 1. Проверяем наличие и валидность бинарников Windows и Android
$exePath = Join-Path $DistDir 'H3HCam Receiver.exe'
$apkPath = Join-Path $DistDir 'H3H-Cam-4.0.6.apk'

if (-not (Test-Path $exePath)) {
    Write-Host "Сборка H3HCam Receiver.exe..." -ForegroundColor Yellow
    & (Join-Path $s8Root 'Build.ps1') -Target Windows
    if ($DistDir -ne (Join-Path $s8Root 'dist')) { Copy-Item -LiteralPath (Join-Path $s8Root 'dist\H3HCam Receiver.exe') -Destination $exePath }
}
Assert-ReleaseExe $exePath

if (-not (Test-Path $apkPath) -or -not (Test-ApkIsRelease $apkPath)) {
    Write-Host "Сборка проверенного Android Release APK..." -ForegroundColor Yellow
    & (Join-Path $s8Root 'Build.ps1') -Target Android
    if ($DistDir -ne (Join-Path $s8Root 'dist')) { Copy-Item -LiteralPath (Join-Path $s8Root 'dist\H3H-Cam-4.0.6.apk') -Destination $apkPath -Force }
}
Assert-ReleaseApk $apkPath

# 2. Ищем необходимые утилиты (ADB, FFmpeg, FFplay)
Write-Host "Поиск внешних утилит (ADB, FFmpeg, FFplay)..." -ForegroundColor Gray

$adbPath = if ($AdbDir) { Join-Path $AdbDir 'adb.exe' } else { $null }
if (-not $adbPath) { $adbPath = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter "adb.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
if (-not $adbPath) { $adbPath = (Get-ChildItem "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
if (-not $adbPath) { $adbPath = (Get-Command adb.exe -ErrorAction SilentlyContinue).Source }

$ffmpegPath = if ($FfmpegDir) { Join-Path $FfmpegDir 'bin\ffmpeg.exe' } else { (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source }
if (-not $ffmpegPath) { $ffmpegPath = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter "ffmpeg.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }

$ffplayPath = if ($FfmpegDir) { Join-Path $FfmpegDir 'bin\ffplay.exe' } else { (Get-Command ffplay.exe -ErrorAction SilentlyContinue).Source }
if (-not $ffplayPath) { $ffplayPath = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter "ffplay.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }

if (-not $adbPath -or -not (Test-Path $adbPath)) { throw "Не найден adb.exe!" }
if (-not $ffmpegPath -or -not (Test-Path $ffmpegPath)) { throw "Не найден ffmpeg.exe!" }
if (-not $ffplayPath -or -not (Test-Path $ffplayPath)) { throw "Не найден ffplay.exe!" }

$adbDir = Split-Path $adbPath -Parent
$ffmpegRoot = Split-Path (Split-Path $ffmpegPath -Parent) -Parent
foreach ($entry in $dependencyManifest.adb.files.PSObject.Properties) {
    Assert-Dependency (Join-Path $adbDir $entry.Name) $entry.Value
}
Assert-Dependency $ffmpegPath $dependencyManifest.ffmpeg.files.'ffmpeg.exe'
Assert-Dependency $ffplayPath $dependencyManifest.ffmpeg.files.'ffplay.exe'
if ((Split-Path $ffplayPath -Parent) -ne (Split-Path $ffmpegPath -Parent)) { throw 'FFmpeg and FFplay must come from the same upstream package' }
foreach ($notice in @((Join-Path $adbDir 'NOTICE.txt'), (Join-Path $adbDir 'source.properties'), (Join-Path $ffmpegRoot 'LICENSE'), (Join-Path $ffmpegRoot 'README.txt'))) {
    if (-not (Test-Path -LiteralPath $notice -PathType Leaf)) { throw "Upstream notice missing: $notice" }
}
if (-not $ModelPath) { $ModelPath = Join-Path $s8Root 'windows\models\u2netp.onnx' }
Assert-Dependency $ModelPath $dependencyManifest.model.sha256

# 3. Формируем папку пакета
$portableDir = Join-Path $DistDir 'H3H-Cam-4.0.12-Portable'
$portableDir = [IO.Path]::GetFullPath($portableDir)
if ([IO.Path]::GetDirectoryName($portableDir) -ne $DistDir.TrimEnd('\')) {
    throw "Portable target must be a direct child of DistDir: $portableDir"
}
if (Test-Path -LiteralPath $portableDir) {
    if ((Get-Item -LiteralPath $portableDir).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to replace a linked portable directory' }
    Remove-Item -LiteralPath $portableDir -Recurse -Force
}
New-Item -ItemType Directory -Path $portableDir -Force | Out-Null

$toolsDir = Join-Path $portableDir 'tools'
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

# Копируем основные файлы
Write-Host "Копирование H3HCam Receiver.exe и H3H-Cam-4.0.6.apk..." -ForegroundColor Gray
Copy-Item -LiteralPath $exePath -Destination (Join-Path $portableDir 'H3HCam Receiver.exe') -Force
Copy-Item -LiteralPath $apkPath -Destination (Join-Path $portableDir 'H3H-Cam-4.0.6.apk') -Force
if (Test-Path (Join-Path $s8Root 'windows\app.ico')) {
    Copy-Item -LiteralPath (Join-Path $s8Root 'windows\app.ico') -Destination (Join-Path $portableDir 'app.ico') -Force
}
$portModels = Join-Path $portableDir 'models'
New-Item -ItemType Directory -Force -Path $portModels | Out-Null
Copy-Item -LiteralPath $ModelPath -Destination (Join-Path $portModels 'u2netp.onnx') -Force
$libusbSrc = Join-Path $s8Root 'windows\native\win-x64\libusb-1.0.dll'
Copy-Item -LiteralPath $libusbSrc -Destination (Join-Path $portableDir 'libusb-1.0.dll') -Force

# Копируем инструкции и памятку напрямую без перекодирования
$instructionSrc = Join-Path $s8Root 'INSTRUCTION-RU.md'
if (Test-Path $instructionSrc) {
    Copy-Item -LiteralPath $instructionSrc -Destination (Join-Path $portableDir 'INSTRUCTION-RU.md') -Force
}
$readmeSrc = Join-Path $s8Root 'packaging\PORTABLE-README.txt'
if (Test-Path $readmeSrc) {
    Copy-Item -LiteralPath $readmeSrc -Destination (Join-Path $portableDir 'README.txt') -Force
}

Copy-Item -LiteralPath (Join-Path $s8Root 'docs') -Destination (Join-Path $portableDir 'docs') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $s8Root 'Collect-Diagnostics.ps1') -Destination $portableDir -Force
foreach ($name in @('LICENSE', 'THIRD_PARTY.md', 'README.md', 'README.en.md')) {
    Copy-Item -LiteralPath (Join-Path $s8Root $name) -Destination $portableDir -Force
}
Copy-Item -LiteralPath (Join-Path $s8Root 'licenses') -Destination $portableDir -Recurse -Force
Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'LICENSE') -Destination (Join-Path $portableDir 'licenses\FFmpeg-GPL-3.0.txt') -Force
Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'README.txt') -Destination (Join-Path $portableDir 'licenses\FFmpeg-Build-9.0.1.txt') -Force
Copy-Item -LiteralPath (Join-Path $adbDir 'NOTICE.txt') -Destination (Join-Path $portableDir 'licenses\Android-Platform-Tools-NOTICE.txt') -Force
Copy-Item -LiteralPath (Join-Path $adbDir 'source.properties') -Destination (Join-Path $portableDir 'licenses\Android-Platform-Tools-Version.txt') -Force
Copy-Item -LiteralPath (Join-Path $s8Root 'packaging\portable-dependencies.json') -Destination (Join-Path $portableDir 'COMPONENTS.json') -Force
Copy-Item -LiteralPath (Join-Path $s8Root 'packaging\PORTABLE-README.en.txt') -Destination (Join-Path $portableDir 'README-English.txt') -Force
foreach ($installerName in @('Установить на телефон.cmd', 'Install-on-phone.cmd')) {
    $installerText = [IO.File]::ReadAllText((Join-Path $s8Root ('packaging\' + $installerName))) -replace '\r?\n', "`r`n"
    [IO.File]::WriteAllText((Join-Path $portableDir $installerName), $installerText, [Text.UTF8Encoding]::new($false))
}
# Копируем утилиты в tools/
Write-Host "Копирование утилит в tools/..." -ForegroundColor Gray
Copy-Item -LiteralPath $adbPath -Destination (Join-Path $toolsDir 'adb.exe') -Force
foreach ($dll in @('AdbWinApi.dll', 'AdbWinUsbApi.dll', 'libwinpthread-1.dll')) {
    $dllPath = Join-Path $adbDir $dll
    Copy-Item -LiteralPath $dllPath -Destination (Join-Path $toolsDir $dll) -Force
}
Copy-Item -LiteralPath $ffmpegPath -Destination (Join-Path $toolsDir 'ffmpeg.exe') -Force
Copy-Item -LiteralPath $ffplayPath -Destination (Join-Path $toolsDir 'ffplay.exe') -Force

$virtualcamSource = Join-Path $s8Root 'windows\native\virtualcam'
$virtualcamTarget = Join-Path $toolsDir 'virtualcam'
New-Item -ItemType Directory -Force -Path $virtualcamTarget | Out-Null
foreach ($cameraModule in @('obs-virtualcam-module64.dll', 'obs-virtualcam-module32.dll')) {
    Copy-Item -LiteralPath (Join-Path $virtualcamSource $cameraModule) -Destination $virtualcamTarget -Force
}


Write-Host "Папка Portable успешно сформирована: $portableDir" -ForegroundColor Green

$hashLines = Get-ChildItem -LiteralPath $portableDir -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($portableDir.Length + 1).Replace('\', '/')
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
}
[IO.File]::WriteAllLines((Join-Path $portableDir 'SHA256SUMS.txt'), [string[]]$hashLines, [Text.UTF8Encoding]::new($false))

# 4. Создаем ZIP-архив со стандартными разделителями '/'
if (-not $NoZip) {
    $zipPath = Join-Path $DistDir 'H3H-Cam-4.0.12-Portable.zip'
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Write-Host "Сжатие в ZIP-архив: $zipPath ..." -ForegroundColor Yellow

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipArchive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $prefixLen = $portableDir.Length + 1
        Get-ChildItem -Path $portableDir -Recurse | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
            $relPath = $_.FullName.Substring($prefixLen).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zipArchive,
                $_.FullName,
                $relPath,
                [System.IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    } finally {
        $zipArchive.Dispose()
    }

    $zipItem = Get-Item $zipPath
    $zipMb = [math]::Round($zipItem.Length / 1MB, 1)
    Write-Host "Архив готов: $zipPath ($($zipMb) MB)" -ForegroundColor Green

    $sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash
    Write-Host "SHA256: $sha" -ForegroundColor Cyan
}

Write-Host "=== Сборка Portable завершена успешно! ===" -ForegroundColor Green
