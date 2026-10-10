param(
    [string]$ReceiverPath = (Join-Path $PSScriptRoot 'H3HCam Receiver.exe'),
    [switch]$Launch,
    [switch]$SafeMode
)
$ErrorActionPreference = 'Stop'
$camData = if ($env:H3HCAM_DATA_DIR) { [IO.Path]::GetFullPath($env:H3HCAM_DATA_DIR) } else { Join-Path $env:LOCALAPPDATA 'H3HCam' }
$reportDir = Join-Path $camData 'diagnostics'
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
$report = Join-Path $reportDir ('H3HCam-diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
$lines = New-Object 'System.Collections.Generic.List[string]'
$lines.Add('H3H Cam local diagnostic report')
$lines.Add('Time: ' + (Get-Date -Format o))
$lines.Add('Windows: ' + [Environment]::OSVersion.VersionString)
$lines.Add('64-bit OS/process: ' + [Environment]::Is64BitOperatingSystem + '/' + [Environment]::Is64BitProcess)
$eventStart = (Get-Date).AddMinutes(-15)
try {
    $os = Get-CimInstance Win32_OperatingSystem
    $lines.Add('Windows edition/build: ' + $os.Caption + ' / ' + $os.Version + ' / ' + $os.BuildNumber)
    foreach ($gpu in Get-CimInstance Win32_VideoController) { $lines.Add('GPU/driver: ' + $gpu.Name + ' / ' + $gpu.DriverVersion) }
} catch { $lines.Add('System information unavailable: ' + $_.Exception.Message) }
if (Test-Path -LiteralPath $ReceiverPath) {
    $exe = Get-Item -LiteralPath $ReceiverPath
    $lines.Add('Receiver version: ' + $exe.VersionInfo.FileVersion)
    $lines.Add('Receiver SHA256: ' + (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash)
    $lines.Add('libusb beside EXE: ' + (Test-Path -LiteralPath (Join-Path $exe.DirectoryName 'libusb-1.0.dll')))
    if ($Launch) {
        if (Get-Process -Name 'H3HCam Receiver' -ErrorAction SilentlyContinue) {
            $lines.Add('Launch skipped: another H3H Cam process is already running.')
        } else {
            try {
                $eventStart = (Get-Date).AddSeconds(-2)
                $launchOptions = @{FilePath=$exe.FullName; WorkingDirectory=$exe.DirectoryName; PassThru=$true}
                if ($SafeMode) { $launchOptions.ArgumentList = '--safe-mode' }
                $watch = [Diagnostics.Stopwatch]::StartNew()
                $child = Start-Process @launchOptions
                $null = $child.Handle
                $lines.Add('Started process ID: ' + $child.Id + '; safe mode: ' + [bool]$SafeMode)
                if ($child.WaitForExit(30000)) {
                    $code = $child.ExitCode
                    $hex = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$code),0).ToString('X8')
                    $lines.Add('Exit after ' + $watch.Elapsed.TotalSeconds.ToString('F2') + ' seconds; code=' + $code + '; hex=0x' + $hex)
                } else { $lines.Add('Receiver is still running after 30 seconds; it was left open.') }
                $child.Dispose()
            } catch { $lines.Add('Launch failed: ' + $_.Exception.Message) }
        }
    }
} else { $lines.Add('Receiver EXE not found; collecting existing logs only.') }
$lines.Add('--- Recent matching Windows Application events ---')
try {
    $events = @(Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$eventStart} -MaxEvents 400 -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in @('Application Error','.NET Runtime','Windows Error Reporting') -and $_.Message -match 'H3HCam|H3H Cam' } |
        Select-Object -First 12)
    if ($events.Count -eq 0) { $lines.Add('No matching recent events found.') }
    foreach ($event in $events) { $lines.Add($event.TimeCreated.ToString('o') + ' / ' + $event.ProviderName + ' / ' + $event.Id); $lines.Add($event.Message) }
} catch { $lines.Add('Event log unavailable: ' + $_.Exception.Message) }
$lines.Add('--- Last 200 lines of the newest application log ---')
try {
    $latest = Get-ChildItem -LiteralPath (Join-Path $camData 'logs') -Filter 'h3hcam-*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) { foreach ($line in Get-Content -LiteralPath $latest.FullName -Tail 200) { $lines.Add($line) } }
    else { $lines.Add('No application log found.') }
} catch { $lines.Add('Application log unavailable: ' + $_.Exception.Message) }
$lines.Add('Report is local. Review paths/device identifiers before posting it publicly. Nothing was uploaded.')
[IO.File]::WriteAllLines($report,$lines,[Text.UTF8Encoding]::new($true))
Write-Host ('Diagnostic report saved: ' + $report)
