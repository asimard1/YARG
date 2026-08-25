param (
    [int]$EditorPid,
    [string]$UnityExe,
    [string]$ProjectPath,
    [string]$BuildsRoot
)

$ErrorActionPreference = "Stop"

# Track active process globally so exit events can clean it up
$global:currentUnityProcess = $null

function Stop-ActiveUnityProcess {
    if ($global:currentUnityProcess -and -not $global:currentUnityProcess.HasExited) {
        try {
            $global:currentUnityProcess.Kill()
        } catch {}
    }
}

# Clean up process if window is closed (X button) or script aborts
[System.AppDomain]::CurrentDomain.add_ProcessExit({ Stop-ActiveUnityProcess })

try {
    Write-Host "Waiting for main Unity Editor process (PID: $EditorPid) to close..." -ForegroundColor Cyan
    if ($EditorPid -gt 0) {
        try {
            Wait-Process -Id $EditorPid -ErrorAction SilentlyContinue
        } catch {}
    }

    Start-Sleep -Seconds 3

    $platforms = @("Windows", "macOS", "Linux")
    $total = $platforms.Count

    for ($i = 0; $i -lt $total; $i++) {
        $platform = $platforms[$i]
        $step = $i + 1
        $percent = [int](($step / $total) * 100)

        Write-Progress -Activity "Nightly Build Pipeline" `
                       -Status "Building $platform ($step of $total)..." `
                       -PercentComplete $percent

        Write-Host "`n========================================" -ForegroundColor Yellow
        Write-Host " Building platform ($step/$total): $platform" -ForegroundColor Yellow
        Write-Host "========================================`n" -ForegroundColor Yellow

        $logFile = Join-Path $BuildsRoot "build_$platform.log"
        if (Test-Path $logFile) { Remove-Item $logFile -Force }

        $global:currentUnityProcess = Start-Process -FilePath $UnityExe -ArgumentList @(
            "-quit",
            "-batchmode",
            "-projectPath", "`"$ProjectPath`"",
            "-executeMethod", "Editor.MakeAllPlatformNightlyBuild.BuildSinglePlatformCLI",
            "-buildTargetArg", $platform,
            "-logFile", "`"$logFile`""
        ) -PassThru

        $lastLine = 0
        while (-not $global:currentUnityProcess.HasExited) {
            if (Test-Path $logFile) {
                $lines = Get-Content $logFile -ErrorAction SilentlyContinue
                if ($lines -and $lines.Count -gt $lastLine) {
                    $lines[$lastLine..($lines.Count - 1)] | ForEach-Object { Write-Host $_ }
                    $lastLine = $lines.Count
                }
            }
            Start-Sleep -Milliseconds 500
        }

        if (Test-Path $logFile) {
            $lines = Get-Content $logFile -ErrorAction SilentlyContinue
            if ($lines -and $lines.Count -gt $lastLine) {
                $lines[$lastLine..($lines.Count - 1)] | ForEach-Object { Write-Host $_ }
            }
        }

        if ($global:currentUnityProcess.ExitCode -eq 0) {
            Write-Host "`n Successfully built $platform!" -ForegroundColor Green
        } else {
            Write-Host "`n Build FAILED for $platform (Exit Code: $($global:currentUnityProcess.ExitCode))." -ForegroundColor Red
        }

        $global:currentUnityProcess = $null
    }

    Write-Progress -Activity "Nightly Build Pipeline" -Status "Completed!" -Completed
    Write-Host "`nAll platform builds completed!" -ForegroundColor Cyan

} finally {
    Stop-ActiveUnityProcess
}