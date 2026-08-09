param(
    [Parameter(Mandatory = $true)][string]$OldExe,
    [Parameter(Mandatory = $true)][string]$NewExe,
    [string]$CurrentVersion = '0.5.9',
    [string]$NewVersion = '0.6.0'
)

$ErrorActionPreference = 'Stop'
$oldSource = (Resolve-Path -LiteralPath $OldExe).Path
$newSource = (Resolve-Path -LiteralPath $NewExe).Path
$parentScript = Join-Path $PSScriptRoot 'updater-integration-parent.ps1'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Stop-ProcessesFromPath([string]$ExecutablePath) {
    $expected = [System.IO.Path]::GetFullPath($ExecutablePath)
    foreach ($process in [System.Diagnostics.Process]::GetProcesses()) {
        try {
            if ($null -ne $process.MainModule -and
                [string]::Equals($process.MainModule.FileName, $expected, [StringComparison]::OrdinalIgnoreCase)) {
                $process.Kill($true)
                $process.WaitForExit(10000) | Out-Null
            }
        }
        catch {
            # Proces mógł zakończyć się między enumeracją i odczytem ścieżki.
        }
        finally {
            $process.Dispose()
        }
    }
}

function Remove-ScenarioRoot([string]$Path) {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            if (-not (Test-Path -LiteralPath $Path)) { return }
            Remove-Item -LiteralPath $Path -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq 10) {
                throw "Nie udało się posprzątać katalogu testowego '$Path': $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}

function Invoke-UpdateScenario([string]$Name, [bool]$SuppressHealth) {
    $scenarioRoot = Join-Path ([System.IO.Path]::GetTempPath()) "KSeF updater ąę $Name-$([Guid]::NewGuid().ToString('N'))"
    $targetDirectory = Join-Path $scenarioRoot 'Aplikacja testowa'
    $updateRoot = Join-Path $targetDirectory '.ksef-update'
    $session = Join-Path $updateRoot "20260803000000-$([Guid]::NewGuid().ToString('N'))"
    [System.IO.Directory]::CreateDirectory($session) | Out-Null

    $nonce = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
    $target = Join-Path $targetDirectory 'KSeFMonitor.exe'
    $helper = Join-Path $session "KSeFMonitor.Updater.$nonce.exe"
    $candidate = Join-Path $session 'KSeFMonitor.exe'
    $pending = Join-Path $targetDirectory ".KSeFMonitor.exe.$nonce.pending"
    $backup = Join-Path $targetDirectory ".KSeFMonitor.exe.$nonce.bak"
    $failed = Join-Path $targetDirectory ".KSeFMonitor.exe.$nonce.failed"
    $descriptorPath = Join-Path $session 'update.json'
    $helperPidPath = Join-Path $session 'helper.pid'
    $readyName = "Local\KSeFMonitor.Update.Ready.$nonce"
    $healthName = "Local\KSeFMonitor.Update.Health.$nonce"

    Copy-Item -LiteralPath $oldSource -Destination $target
    Copy-Item -LiteralPath $oldSource -Destination $helper
    Copy-Item -LiteralPath $newSource -Destination $candidate
    if ($SuppressHealth) { New-Item -ItemType File -Path (Join-Path $session 'suppress-health') | Out-Null }

    $oldHash = Get-Sha256 $target
    $newHash = Get-Sha256 $candidate
    $descriptor = [ordered]@{
        SchemaVersion = 1
        Nonce = $nonce
        CurrentVersion = $CurrentVersion
        NewVersion = $NewVersion
        TargetExePath = $target
        HelperExePath = $helper
        CandidateExePath = $candidate
        PendingExePath = $pending
        BackupExePath = $backup
        FailedExePath = $failed
        DescriptorPath = $descriptorPath
        ParentProcessId = 1
        ParentProcessStartUtcTicks = 1
        CurrentExeSha256 = $oldHash
        NewExeSha256 = $newHash
        ReadyEventName = $readyName
        HealthEventName = $healthName
        CreatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        State = 'Verified'
    }
    $descriptor | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $descriptorPath -Encoding utf8NoBOM

    $ready = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, $readyName)
    $health = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, $healthName)
    $parent = $null
    $helperProcess = $null
    try {
        $start = [System.Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.ArgumentList.Add('-NoProfile')
        $start.ArgumentList.Add('-NonInteractive')
        $start.ArgumentList.Add('-File')
        $start.ArgumentList.Add($parentScript)
        $start.ArgumentList.Add('-DescriptorPath')
        $start.ArgumentList.Add($descriptorPath)
        $start.ArgumentList.Add('-HelperPath')
        $start.ArgumentList.Add($helper)
        $start.ArgumentList.Add('-Nonce')
        $start.ArgumentList.Add($nonce)
        $start.ArgumentList.Add('-ReadyEventName')
        $start.ArgumentList.Add($readyName)
        $start.ArgumentList.Add('-HelperPidPath')
        $start.ArgumentList.Add($helperPidPath)
        $parent = [System.Diagnostics.Process]::Start($start)
        if ($null -eq $parent) { throw 'Nie uruchomiono procesu nadrzędnego testu.' }

        $pidDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while (-not (Test-Path -LiteralPath $helperPidPath)) {
            if ([DateTimeOffset]::UtcNow -ge $pidDeadline) { throw 'Proces nadrzędny nie zapisał PID helpera.' }
            Start-Sleep -Milliseconds 100
        }
        $helperPid = [int](Get-Content -LiteralPath $helperPidPath -Raw)
        try {
            $helperProcess = [System.Diagnostics.Process]::GetProcessById($helperPid)
        }
        catch {
            throw "Helper o PID $helperPid zakończył się przed zgłoszeniem gotowości."
        }

        if (-not $ready.WaitOne([TimeSpan]::FromSeconds(130))) { throw 'Helper nie zgłosił gotowości.' }
        if (-not $parent.WaitForExit(10000)) {
            throw 'Proces nadrzędny testu nie zakończył się w ciągu 10 sekund.'
        }
        if ($parent.ExitCode -ne 0) {
            throw "Proces nadrzędny testu zakończył się kodem $($parent.ExitCode)."
        }
        if (-not $helperProcess.WaitForExit(180000)) {
            $helperProcess.Kill($true)
            throw 'Helper aktualizacji nie zakończył się w limicie 180 sekund.'
        }

        $expectedExit = if ($SuppressHealth) { 31 } else { 0 }
        if ($helperProcess.ExitCode -ne $expectedExit) {
            throw "Scenariusz $Name: helper zwrócił $($helperProcess.ExitCode), oczekiwano $expectedExit."
        }
        $final = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
        if ($SuppressHealth) {
            if ($final.State -cne 'RolledBack' -or (Get-Sha256 $target) -cne $oldHash -or
                -not (Test-Path -LiteralPath $failed) -or (Get-Sha256 $failed) -cne $newHash) {
                throw 'Scenariusz rollback nie przywrócił dokładnie poprzedniego EXE.'
            }
        }
        else {
            if ($final.State -cne 'Healthy' -or (Get-Sha256 $target) -cne $newHash -or
                -not (Test-Path -LiteralPath $backup) -or (Get-Sha256 $backup) -cne $oldHash -or
                (Test-Path -LiteralPath $failed)) {
                throw 'Scenariusz sukcesu nie zachował poprawnego targetu i backupu.'
            }
        }
    }
    finally {
        $ready.Dispose()
        $health.Dispose()
        if ($null -ne $helperProcess) { $helperProcess.Dispose() }
        if ($null -ne $parent) {
            if (-not $parent.HasExited) { $parent.Kill($true) }
            $parent.Dispose()
        }
        Start-Sleep -Milliseconds 500
        Stop-ProcessesFromPath $target
        Stop-ProcessesFromPath $helper
        Start-Sleep -Milliseconds 500
        Remove-ScenarioRoot $scenarioRoot
    }
}

Invoke-UpdateScenario -Name 'success' -SuppressHealth $false
Invoke-UpdateScenario -Name 'rollback' -SuppressHealth $true
Write-Host 'Updater integration tests: OK'
