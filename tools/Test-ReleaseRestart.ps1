param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Fixture,
    [int]$Count = 1,
    [switch]$CloseOnly
)
$ErrorActionPreference = 'Stop'
$exePath = [IO.Path]::GetFullPath($Executable)
$fixturePath = [IO.Path]::GetFullPath($Fixture)
if ((Split-Path $fixturePath -Parent) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or
    (Split-Path $fixturePath -Leaf) -notlike 'WebSiteMonitor-restart-probe-*') { throw 'Not an isolated fixture' }
$initial = [Diagnostics.ProcessStartInfo]::new($exePath)
$initial.UseShellExecute = $false
$initial.CreateNoWindow = $true
$initial.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$initial.WorkingDirectory = Split-Path $exePath -Parent
$initial.ArgumentList.Add('--restart-probe=' + $fixturePath)
$first = [Diagnostics.Process]::Start($initial)
$firstPid = $first.Id
$first.Dispose()
$timer = [Diagnostics.Stopwatch]::StartNew()
$phase = [Diagnostics.Stopwatch]::StartNew()
$observed = 0
$tracked = @{}
$recordPath = Join-Path $fixturePath 'iterations.csv'
$success = Join-Path $fixturePath 'SUCCESS'
$failure = Join-Path $fixturePath 'FAILED'
try {
    while ($true) {
        if (Test-Path -LiteralPath $failure) { throw ('Probe failed: ' + [IO.File]::ReadAllText($failure)) }
        $lines = @()
        if (Test-Path -LiteralPath $recordPath) {
            $stream = [IO.File]::Open($recordPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $reader = [IO.StreamReader]::new($stream)
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $parts = @($text -split '\r?\n')
            # A writer may be appending the last line. Only complete records are evidence.
            if ($parts.Count -gt 1) { $lines = @($parts[0..($parts.Count - 2)] | Where-Object { $_.Length -gt 0 }) }
        }
        if ($lines.Count -gt $observed) {
            for ($i = $observed; $i -lt $lines.Count; $i++) {
                Write-Output ('ITERATION ' + $lines[$i])
                $fields = $lines[$i].Split(',')
                if ($fields[3] -ne 'True' -or $fields[4] -ne 'True' -or $fields[5] -ne 'True' -or $fields[6] -ne 'True') { throw 'Invariant failed' }
                if ($fields[1] -eq $fields[2]) { throw 'PID did not change' }
            }
            $observed = $lines.Count
            $phase.Restart()
        }
        foreach ($candidate in [Diagnostics.Process]::GetProcessesByName([IO.Path]::GetFileNameWithoutExtension($exePath))) {
            if (-not $tracked.ContainsKey($candidate.Id)) {
                try { $null = $candidate.SafeHandle; $tracked[$candidate.Id] = $candidate }
                catch { $candidate.Dispose() } # Enumeration may include a process that has just exited.
            } else { $candidate.Dispose() }
        }
        if ((Test-Path -LiteralPath $success) -and ($CloseOnly -or $observed -eq $Count)) { break }
        if ($phase.Elapsed.TotalSeconds -gt 45) { throw ('Phase timeout after iteration ' + $observed) }
        Start-Sleep -Milliseconds 100
    }
    $end = [Diagnostics.Stopwatch]::StartNew()
    while (@([Diagnostics.Process]::GetProcessesByName([IO.Path]::GetFileNameWithoutExtension($exePath))).Count -ne 0 -and $end.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    $remaining = @([Diagnostics.Process]::GetProcessesByName([IO.Path]::GetFileNameWithoutExtension($exePath)))
    if ($remaining.Count -ne 0) { throw 'Own EXE processes remain' }
    if (-not $CloseOnly -and $observed -ne $Count) { throw 'Wrong iteration count' }
    foreach ($entry in $tracked.GetEnumerator()) {
        if (-not $entry.Value.HasExited -or $entry.Value.ExitCode -ne 0) { throw ('Nonzero exit: ' + $entry.Key) }
    }
    Write-Output ("SUCCESS firstPID=$firstPid restarts=$observed elapsedSeconds=" + [Math]::Round($timer.Elapsed.TotalSeconds, 2) + " tracked=" + $tracked.Count + " residue=0")
} finally {
    foreach ($entry in $tracked.GetEnumerator()) { $entry.Value.Dispose() }
}
