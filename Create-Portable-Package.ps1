param(
    [string]$DistDir = '',
    [switch]$NoZip
)
$ErrorActionPreference = 'Stop'
$s8Root = $PSScriptRoot
if (-not $DistDir) { $DistDir = Join-Path $s8Root 'dist' }
$DistDir = [IO.Path]::GetFullPath($DistDir)

Write-Host "=== Создание автономного Portable-пакета H3H Cam 4.0.9 ===" -ForegroundColor Cyan

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
        if (-not ($badgingStr -match "versionCode='17'")) { return $false }
        if (-not ($badgingStr -match "versionName='4\.0\.4'")) { return $false }
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
        if (-not ($badgingStr -match "versionCode='17'")) {
            throw "RELEASE GUARD FAILED: versionCode must be 17"
        }
        if (-not ($badgingStr -match "versionName='4\.0\.4'")) {
            throw "RELEASE GUARD FAILED: versionName must be 4.0.4"
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
    if ($info.FileVersion -ne '4.0.9.0') {
        throw "RELEASE GUARD FAILED: Receiver FileVersion must be 4.0.9.0 (got: $($info.FileVersion))"
    }
    Write-Host "✅ Receiver Release Guard: $ExePath подтверждён (Version: $($info.FileVersion))." -ForegroundColor Green
}

# 1. Проверяем наличие и валидность бинарников Windows и Android
$exePath = Join-Path $DistDir 'H3HCam Receiver.exe'
$apkPath = Join-Path $DistDir 'H3H-Cam-4.0.4.apk'

if (-not (Test-Path $exePath)) {
    Write-Host "Сборка H3HCam Receiver.exe..." -ForegroundColor Yellow
    & (Join-Path $s8Root 'Build.ps1') -Target Windows
    if ($DistDir -ne (Join-Path $s8Root 'dist')) { Copy-Item -LiteralPath (Join-Path $s8Root 'dist\H3HCam Receiver.exe') -Destination $exePath }
}
Assert-ReleaseExe $exePath

if (-not (Test-Path $apkPath) -or -not (Test-ApkIsRelease $apkPath)) {
    Write-Host "Сборка проверенного Android Release APK..." -ForegroundColor Yellow
    & (Join-Path $s8Root 'Build.ps1') -Target Android
    if ($DistDir -ne (Join-Path $s8Root 'dist')) { Copy-Item -LiteralPath (Join-Path $s8Root 'dist\H3H-Cam-4.0.4.apk') -Destination $apkPath -Force }
}
Assert-ReleaseApk $apkPath

# 2. Ищем необходимые утилиты (ADB, FFmpeg, FFplay)
Write-Host "Поиск внешних утилит (ADB, FFmpeg, FFplay)..." -ForegroundColor Gray

$adbPath = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter "adb.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
if (-not $adbPath) { $adbPath = (Get-ChildItem "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
if (-not $adbPath) { $adbPath = (Get-Command adb.exe -ErrorAction SilentlyContinue).Source }

$ffmpegPath = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source
if (-not $ffmpegPath) { $ffmpegPath = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter "ffmpeg.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }

$ffplayPath = (Get-Command ffplay.exe -ErrorAction SilentlyContinue).Source
if (-not $ffplayPath) { $ffplayPath = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter "ffplay.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }

if (-not $adbPath -or -not (Test-Path $adbPath)) { throw "Не найден adb.exe!" }
if (-not $ffmpegPath -or -not (Test-Path $ffmpegPath)) { throw "Не найден ffmpeg.exe!" }
if (-not $ffplayPath -or -not (Test-Path $ffplayPath)) { throw "Не найден ffplay.exe!" }

$adbDir = Split-Path $adbPath -Parent

# 3. Формируем папку пакета
$portableDir = Join-Path $DistDir 'H3H-Cam-4.0.9-Portable'
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
Write-Host "Копирование H3HCam Receiver.exe и H3H-Cam-4.0.4.apk..." -ForegroundColor Gray
Copy-Item -LiteralPath $exePath -Destination (Join-Path $portableDir 'H3HCam Receiver.exe') -Force
Copy-Item -LiteralPath $apkPath -Destination (Join-Path $portableDir 'H3H-Cam-4.0.4.apk') -Force
if (Test-Path (Join-Path $s8Root 'windows\app.ico')) {
    Copy-Item -LiteralPath (Join-Path $s8Root 'windows\app.ico') -Destination (Join-Path $portableDir 'app.ico') -Force
}
if (Test-Path (Join-Path $s8Root 'windows\models')) {
    $portModels = Join-Path $portableDir 'models'
    New-Item -ItemType Directory -Force -Path $portModels | Out-Null
    Copy-Item -Path (Join-Path $s8Root 'windows\models\*') -Destination $portModels -Recurse -Force
}
$libusbSrc = Join-Path $s8Root 'windows\native\win-x64\libusb-1.0.dll'
if (Test-Path $libusbSrc) {
    Copy-Item -LiteralPath $libusbSrc -Destination (Join-Path $portableDir 'libusb-1.0.dll') -Force
}

# Копируем инструкции и памятку напрямую без перекодирования
$instructionSrc = Join-Path $s8Root 'INSTRUCTION-RU.md'
if (Test-Path $instructionSrc) {
    Copy-Item -LiteralPath $instructionSrc -Destination (Join-Path $portableDir 'INSTRUCTION-RU.md') -Force
}
$readmeSrc = Join-Path $s8Root 'README.txt'
if (Test-Path $readmeSrc) {
    Copy-Item -LiteralPath $readmeSrc -Destination (Join-Path $portableDir 'README.txt') -Force
}

foreach ($doc in @('AUDIT-4.0.1.md', 'QUICKSTART-4.0.5.md', 'WIFI-FIX-4.0.2.md', 'PACING-FIX-4.0.3.md', 'LATENCY-FIX-4.0.4.md', 'PRIORITY-4.0.5.md', 'docs/PERFORMANCE.md', 'docs/QUICKSTART.md')) {
    $documentPath = Join-Path $s8Root $doc
    if (Test-Path -LiteralPath $documentPath) { Copy-Item -LiteralPath $documentPath -Destination $portableDir }
}
Copy-Item -LiteralPath (Join-Path $s8Root 'docs') -Destination (Join-Path $portableDir 'docs') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $s8Root 'Collect-Diagnostics.ps1') -Destination $portableDir -Force
# Копируем утилиты в tools/
Write-Host "Копирование утилит в tools/..." -ForegroundColor Gray
Copy-Item -LiteralPath $adbPath -Destination (Join-Path $toolsDir 'adb.exe') -Force
foreach ($dll in @('AdbWinApi.dll', 'AdbWinUsbApi.dll', 'libwinpthread-1.dll')) {
    $dllPath = Join-Path $adbDir $dll
    if (Test-Path $dllPath) {
        Copy-Item -LiteralPath $dllPath -Destination (Join-Path $toolsDir $dll) -Force
    }
}
Copy-Item -LiteralPath $ffmpegPath -Destination (Join-Path $toolsDir 'ffmpeg.exe') -Force
Copy-Item -LiteralPath $ffplayPath -Destination (Join-Path $toolsDir 'ffplay.exe') -Force

if (Test-Path (Join-Path $s8Root 'windows\tools\virtualcam')) {
    Copy-Item -LiteralPath (Join-Path $s8Root 'windows\tools\virtualcam') -Destination $toolsDir -Recurse -Force
}

Write-Host "Папка Portable успешно сформирована: $portableDir" -ForegroundColor Green

$hashLines = Get-ChildItem -LiteralPath $portableDir -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($portableDir.Length + 1).Replace('\', '/')
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
}
[IO.File]::WriteAllLines((Join-Path $portableDir 'SHA256SUMS.txt'), [string[]]$hashLines, [Text.UTF8Encoding]::new($false))

# 4. Создаем ZIP-архив со стандартными разделителями '/'
if (-not $NoZip) {
    $zipPath = Join-Path $DistDir 'H3H-Cam-4.0.9-Portable.zip'
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
