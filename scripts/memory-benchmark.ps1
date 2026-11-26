[CmdletBinding()]
param(
    [ValidateRange(1, 10)]
    [int]$Rounds = 3,
    [ValidateRange(5, 600)]
    [int]$WarmupSeconds = 45,
    [ValidateRange(3, 300)]
    [int]$SamplesPerHost = 15,
    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 2
)

$ErrorActionPreference = "Stop"

$hosts = @(
    [pscustomobject]@{
        Name = "WinForms"
        Executable = Join-Path $PSScriptRoot "winforms\KikitanTranslator.exe"
    },
    [pscustomobject]@{
        Name = "Uno"
        Executable = Join-Path $PSScriptRoot "uno\KikiVRCTan.exe"
    }
)

foreach ($targetHost in $hosts) {
    if (-not (Test-Path -LiteralPath $targetHost.Executable -PathType Leaf)) {
        throw "Missing $($targetHost.Name) executable: $($targetHost.Executable)"
    }
}

$existingHosts = @(Get-Process -Name "KikiVRCTan", "KikitanTranslator" -ErrorAction SilentlyContinue)
if ($existingHosts.Count -gt 0) {
    $runningNames = ($existingHosts | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ", "
    throw "Close existing KikiVRCTan/WinForms host processes before testing: $runningNames"
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

for ($round = 1; $round -le $Rounds; $round++) {
    $orderedHosts = if ($round % 2 -eq 1) { $hosts } else { @($hosts[1], $hosts[0]) }

    foreach ($targetHost in $orderedHosts) {
        Write-Host "Round $round/$Rounds - $($targetHost.Name): launching. Leave the app idle; do not press Start."
        $process = Start-Process -FilePath $targetHost.Executable `
            -WorkingDirectory (Split-Path -Parent $targetHost.Executable) -PassThru
        $processId = $process.Id

        try {
            $windowDeadline = (Get-Date).AddSeconds(60)
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
                throw "$($targetHost.Name) did not create a main window within 60 seconds."
            }

            Start-Sleep -Seconds $WarmupSeconds

            for ($sample = 1; $sample -le $SamplesPerHost; $sample++) {
                $memory = Get-MemoryCounters -ProcessId $processId
                $results.Add([pscustomobject]@{
                    Host = $targetHost.Name
                    Round = $round
                    Sample = $sample
                    Time = (Get-Date).ToString("o")
                    ProcessId = $processId
                    WorkingSetMiB = $memory.WorkingSetMiB
                    PrivateWorkingSetMiB = $memory.PrivateWorkingSetMiB
                    PrivateBytesMiB = $memory.PrivateBytesMiB
                })
                Start-Sleep -Seconds $IntervalSeconds
            }
        }
        finally {
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

$csvPath = Join-Path $PSScriptRoot "memory-benchmark-results.csv"
$summaryPath = Join-Path $PSScriptRoot "memory-benchmark-summary.csv"
$results | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8 -Force

$summary = foreach ($group in ($results | Group-Object Host)) {
    $set = @($group.Group)
    $workingSetMedian = Get-Median -Values @($set | ForEach-Object { $_.WorkingSetMiB })
    $privateWorkingSetMedian = Get-Median -Values @($set | ForEach-Object { $_.PrivateWorkingSetMiB })
    $privateBytesMedian = Get-Median -Values @($set | ForEach-Object { $_.PrivateBytesMiB })

    [pscustomobject]@{
        Host = $group.Name
        Samples = $set.Count
        MedianWorkingSetMiB = [math]::Round($workingSetMedian, 1)
        MedianPrivateWorkingSetMiB = [math]::Round($privateWorkingSetMedian, 1)
        MedianPrivateBytesMiB = [math]::Round($privateBytesMedian, 1)
    }
}

$summary | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding UTF8 -Force
Write-Host ""
Write-Host "Median private working set by host:"
$summary | Format-Table -AutoSize
Write-Host "Raw samples: $csvPath"
Write-Host "Summary: $summaryPath"
