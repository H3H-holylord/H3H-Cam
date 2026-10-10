param(
    [ValidateSet('All','Android','Windows','Test','Portable')][string]$Target = 'All',
    [string]$DotNet = '',
    [string]$JavaHome = ''
)
$ErrorActionPreference = 'Stop'
$s8Root = $PSScriptRoot
$s8Dist = Join-Path $s8Root 'dist'
New-Item -ItemType Directory -Force -Path $s8Dist | Out-Null

function Get-AndroidTool([string]$ToolName) {
    $sdk = $env:ANDROID_HOME
    if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
    $found = Get-ChildItem -Path (Join-Path $sdk 'build-tools') -Filter $ToolName -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.FullName }
    $cmd = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
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
    } else {
        Write-Warning "aapt.exe not found for deep badging inspection."
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
    Write-Host "✅ APK Release Guard: $ApkPath verified (release, non-debuggable, signed)." -ForegroundColor Green
}

if (-not $DotNet) {
    $localDotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet) {
        $DotNet = $localDotnet
        $env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA 'dotnet'
        $env:PATH = "$($env:DOTNET_ROOT);$($env:PATH)"
    } else {
        $DotNet = 'dotnet'
    }
}

if ($Target -in @('All','Android')) {
    if ($JavaHome) { $env:JAVA_HOME = $JavaHome }
    elseif (-not $env:JAVA_HOME) {
        $s8Jbr = Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'
        if (Test-Path -LiteralPath $s8Jbr) { $env:JAVA_HOME = $s8Jbr }
    }
    if (-not $env:ANDROID_HOME) { $env:ANDROID_HOME = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }

    Write-Host "Сборка Android Release APK (assembleRelease)..." -ForegroundColor Cyan
    & (Join-Path $s8Root 'android\gradlew.bat') -p (Join-Path $s8Root 'android') assembleRelease testDebugUnitTest --console=plain
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed' }

    $apkSource = Join-Path $s8Root 'android\app\build\outputs\apk\release\app-release.apk'
    if (-not (Test-Path $apkSource)) {
        throw "Release APK build failed: app-release.apk not found at $apkSource. Refusing to fallback to debug!"
    }

    Assert-ReleaseApk $apkSource

    Copy-Item -LiteralPath $apkSource -Destination (Join-Path $s8Dist 'H3H-Cam-4.0.6.apk') -Force
    Copy-Item -LiteralPath (Join-Path $s8Dist 'H3H-Cam-4.0.6.apk') -Destination (Join-Path $s8Root 'H3H-Cam-4.0.6.apk') -Force
    Write-Host "Android Release APK готов: $(Join-Path $s8Dist 'H3H-Cam-4.0.6.apk')" -ForegroundColor Green
}
if ($Target -in @('All','Test')) {
    Write-Host "Запуск тестов Windows..." -ForegroundColor Cyan
    & $DotNet run --project (Join-Path $s8Root 'tests\S8Cam.Tests.csproj') -c Release -- unit (Join-Path $s8Dist 'test-results')
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed' }
}
if ($Target -in @('All','Windows')) {
    Write-Host "Публикация Windows Receiver (win-x64 self-contained)..." -ForegroundColor Cyan
    & $DotNet publish (Join-Path $s8Root 'windows\S8Cam.Receiver.csproj') -c Release -r win-x64 --self-contained true -o $s8Dist
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }

    $receiverExe = Join-Path $s8Dist 'H3HCam Receiver.exe'
    if (Test-Path $receiverExe) {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($receiverExe)
        if ($info.FileVersion -ne '4.0.12.0') {
            throw "RELEASE GUARD FAILED: Receiver FileVersion must be 4.0.12.0 (got: $($info.FileVersion))"
        }
        Write-Host "✅ Receiver Release Guard: $receiverExe verified (Version: $($info.FileVersion))." -ForegroundColor Green
        try {
            Copy-Item -LiteralPath $receiverExe -Destination (Join-Path $s8Root 'H3HCam Receiver.exe') -Force
            Copy-Item -LiteralPath (Join-Path $s8Dist 'libusb-1.0.dll') -Destination (Join-Path $s8Root 'libusb-1.0.dll') -Force
            $s8RootVirtualcam = Join-Path $s8Root 'tools\virtualcam'
            New-Item -ItemType Directory -Force -Path $s8RootVirtualcam | Out-Null
            foreach ($s8Module in @('obs-virtualcam-module64.dll', 'obs-virtualcam-module32.dll')) {
                Copy-Item -LiteralPath (Join-Path $s8Dist ('tools\virtualcam\' + $s8Module)) -Destination $s8RootVirtualcam -Force
            }
        } catch {
            Write-Warning "Could not overwrite root H3HCam Receiver.exe (process might be running): $_"
        }
    }
    if (Test-Path (Join-Path $s8Root 'windows\models')) {
        $distModels = Join-Path $s8Dist 'models'
        New-Item -ItemType Directory -Force -Path $distModels | Out-Null
        Copy-Item -Path (Join-Path $s8Root 'windows\models\*') -Destination $distModels -Recurse -Force
        $rootModels = Join-Path $s8Root 'models'
        New-Item -ItemType Directory -Force -Path $rootModels | Out-Null
        Copy-Item -Path (Join-Path $s8Root 'windows\models\*') -Destination $rootModels -Recurse -Force
    }
}
if ($Target -eq 'Portable') {
    & (Join-Path $s8Root 'Create-Portable-Package.ps1') -DistDir $s8Dist
}
Write-Output "Ready: $s8Dist"
