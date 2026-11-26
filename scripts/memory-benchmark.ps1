[CmdletBinding()]
param(
    [ValidateSet("Idle", "Running")]
    [string]$State = "Idle",
    [string]$BenchmarkRoot = (Join-Path $PSScriptRoot "memory-test-hosts"),
    [ValidateSet("", "WinForms", "Uno Skia", "Uno WinAppSDK")]
    [string]$HostName = "",
    [ValidateRange(0, 2147483647)]
    [int]$ExistingProcessId = 0,
    [ValidateRange(0, 10)]
    [int]$RoundNumber = 0,
    [ValidateRange(1, 10)]
    [int]$Rounds = 3,
    [ValidateRange(3, 300)]
    [int]$SamplesPerRound = 15,
    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 2,
    [ValidateRange(30, 300)]
    [int]$ActivationDelaySeconds = 120
)

$ErrorActionPreference = "Stop"
$running = $State -eq "Running"
$warmupSeconds = if ($running) { 300 } else { 45 }

$hosts = @(
    [pscustomobject]@{
        Name = "WinForms"
        Executable = Join-Path $BenchmarkRoot "winforms\KikitanTranslator.exe"
    },
    [pscustomobject]@{
        Name = "Uno Skia"
        Executable = Join-Path $BenchmarkRoot "uno-skia\KikiVRCTan.exe"
    },
    [pscustomobject]@{
        Name = "Uno WinAppSDK"
        Executable = Join-Path $BenchmarkRoot "uno-winappsdk\KikiVRCTan.exe"
    }
)

if ($ExistingProcessId -gt 0) {
    if ([string]::IsNullOrWhiteSpace($HostName)) {
        throw "Specify -HostName when sampling an existing process."
    }
    $hosts = @($hosts | Where-Object { $_.Name -eq $HostName })
    if ($hosts.Count -ne 1) {
        throw "Unknown benchmark host: $HostName"
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($HostName)) {
    throw "-HostName requires -ExistingProcessId."
}

foreach ($targetHost in $hosts) {
    if (-not (Test-Path -LiteralPath $targetHost.Executable -PathType Leaf)) {
        throw "Missing $($targetHost.Name) executable: $($targetHost.Executable)"
    }
    $configPath = Join-Path (Split-Path -Parent $targetHost.Executable) "config.json"
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "Missing $configPath. Copy the existing app config into each benchmark folder; do not upload it."
    }
}

$existingHosts = @(Get-Process -Name "KikiVRCTan", "KikitanTranslator" -ErrorAction SilentlyContinue)
$otherHosts = if ($ExistingProcessId -gt 0) {
    @($existingHosts | Where-Object { $_.Id -ne $ExistingProcessId })
}
else {
    $existingHosts
}
if ($otherHosts.Count -gt 0) {
    $runningNames = ($otherHosts | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ", "
    throw "Close existing KikiVRCTan/WinForms host processes before testing: $runningNames"
}

$existingOverlays = @(Get-Process -Name "KikitanTranslator.Overlay" -ErrorAction SilentlyContinue)
if ($existingOverlays.Count -gt 0) {
    $runningNames = ($existingOverlays | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ", "
    throw "Close overlay processes before testing: $runningNames"
}

function Get-MemoryCounters([int]$ProcessId) {
    $counter = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process `
        -Filter "IDProcess = $ProcessId"
    if ($null -eq $counter) {
        throw "Windows did not return memory counters for PID $ProcessId."
    }

    [pscustomobject]@{
        WorkingSetMiB = [math]::Round($counter.WorkingSet / 1MB, 1)
        PrivateWorkingSetMiB = [math]::Round($counter.WorkingSetPrivate / 1MB, 1)
        PrivateBytesMiB = [math]::Round($counter.PrivateBytes / 1MB, 1)
    }
}

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 0) {
        return ($sorted[$middle - 1] + $sorted[$middle]) / 2
    }

    return $sorted[$middle]
}

$results = [System.Collections.Generic.List[object]]::new()
$csvPath = Join-Path $BenchmarkRoot "memory-benchmark-$State.csv"
$summaryPath = Join-Path $BenchmarkRoot "memory-benchmark-$State-summary.csv"
$priorResults = @()
if ($ExistingProcessId -gt 0 -and (Test-Path -LiteralPath $csvPath -PathType Leaf)) {
    $priorResults = @(Import-Csv -LiteralPath $csvPath)
}
if ($ExistingProcessId -gt 0 -and $RoundNumber -eq 0) {
    $RoundNumber = 1
    if ($priorResults.Count -gt 0) {
        $previousRound = ($priorResults | ForEach-Object { [int]$_.Round } | Measure-Object -Maximum).Maximum
        $RoundNumber = [int]$previousRound + 1
    }
}

for ($round = 1; $round -le $Rounds; $round++) {
    if ($ExistingProcessId -gt 0) {
        $orderedHosts = $hosts
    }
    else {
        $orderedHosts = for ($offset = 0; $offset -lt $hosts.Count; $offset++) {
            $hosts[($round - 1 + $offset) % $hosts.Count]
        }
    }

    foreach ($targetHost in $orderedHosts) {
        if ($running) {
            Write-Host "Round $round/$Rounds - $($targetHost.Name): launch, press Start, and keep both microphone and desktop audio translating."
        }
        else {
            Write-Host "Round $round/$Rounds - $($targetHost.Name): launching idle. Do not press Start."
        }

        if ($ExistingProcessId -gt 0) {
            $process = Get-Process -Id $ExistingProcessId -ErrorAction Stop
            $expectedProcessName = [System.IO.Path]::GetFileNameWithoutExtension($targetHost.Executable)
            if ($process.ProcessName -ne $expectedProcessName) {
                throw "PID $ExistingProcessId is $($process.ProcessName), not $expectedProcessName."
            }
            $processId = $ExistingProcessId
            $sampleRound = $RoundNumber + $round - 1
        }
        else {
            $process = Start-Process -FilePath $targetHost.Executable `
                -WorkingDirectory (Split-Path -Parent $targetHost.Executable) -PassThru
            $processId = $process.Id
            $sampleRound = $round
        }

        try {
            if ($ExistingProcessId -eq 0) {
                $windowDeadline = (Get-Date).AddSeconds(90)
                do {
                    $process.Refresh()
                    if ($process.HasExited) {
                        throw "$($targetHost.Name) exited before its window was ready."
                    }
                    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                        break
                    }
                    Start-Sleep -Seconds 1
                } while ((Get-Date) -lt $windowDeadline)

                if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
                    throw "$($targetHost.Name) did not create a main window within 90 seconds."
                }
            }

            if ($running) {
                Write-Host "Start translation within the next $ActivationDelaySeconds seconds. The five-minute active warmup begins after that."
                Start-Sleep -Seconds $ActivationDelaySeconds
            }

            Write-Host "$State warmup: $warmupSeconds seconds."
            Start-Sleep -Seconds $warmupSeconds
            $process.Refresh()
            if ($process.HasExited) {
                throw "$($targetHost.Name) exited during warmup."
            }

            for ($sample = 1; $sample -le $SamplesPerRound; $sample++) {
                $memory = Get-MemoryCounters -ProcessId $processId
                $results.Add([pscustomobject]@{
                    State = $State
                    Host = $targetHost.Name
                    Round = $sampleRound
                    Sample = $sample
                    Time = (Get-Date).ToString("o")
                    ProcessId = $processId
                    WorkingSetMiB = $memory.WorkingSetMiB
                    PrivateWorkingSetMiB = $memory.PrivateWorkingSetMiB
                    PrivateBytesMiB = $memory.PrivateBytesMiB
                })
                Start-Sleep -Seconds $IntervalSeconds
            }

            $currentResults = $results.ToArray()
            $allResults = @($priorResults) + @($currentResults)
            $allResults | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8 -Force
        }
        finally {
            if ($ExistingProcessId -eq 0) {
                $process.Refresh()
                if (-not $process.HasExited) {
                    $null = $process.CloseMainWindow()
                    if (-not $process.WaitForExit(10000)) {
                        Stop-Process -Id $processId -Force
                    }
                }
            }
        }
    }
}

$currentResults = $results.ToArray()
$allResults = @($priorResults) + @($currentResults)
$allResults | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8 -Force

$summary = foreach ($group in ($allResults | Group-Object Host)) {
    $set = @($group.Group)
    [pscustomobject]@{
        State = $State
        Host = $group.Name
        Samples = $set.Count
        MedianWorkingSetMiB = [math]::Round((Get-Median -Values @($set | ForEach-Object { [double]$_.WorkingSetMiB })), 1)
        MedianPrivateWorkingSetMiB = [math]::Round((Get-Median -Values @($set | ForEach-Object { [double]$_.PrivateWorkingSetMiB })), 1)
        MedianPrivateBytesMiB = [math]::Round((Get-Median -Values @($set | ForEach-Object { [double]$_.PrivateBytesMiB })), 1)
    }
}

$summary | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding UTF8 -Force
Write-Host ""
Write-Host "Median memory counters ($State):"
$summary | Format-Table -AutoSize
Write-Host "Raw samples: $csvPath"
Write-Host "Summary: $summaryPath"
