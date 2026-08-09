param(
    [Parameter(Mandatory = $true)][string]$DescriptorPath,
    [Parameter(Mandatory = $true)][string]$HelperPath,
    [Parameter(Mandatory = $true)][string]$Nonce,
    [Parameter(Mandatory = $true)][string]$ReadyEventName,
    [Parameter(Mandatory = $true)][string]$HelperPidPath
)

$ErrorActionPreference = 'Stop'
$descriptor = Get-Content -LiteralPath $DescriptorPath -Raw | ConvertFrom-Json
$self = [System.Diagnostics.Process]::GetCurrentProcess()
$descriptor.ParentProcessId = $PID
$descriptor.ParentProcessStartUtcTicks = $self.StartTime.ToUniversalTime().Ticks
$descriptor | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $DescriptorPath -Encoding utf8NoBOM

$start = [System.Diagnostics.ProcessStartInfo]::new($HelperPath)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WorkingDirectory = [System.IO.Path]::GetDirectoryName($descriptor.TargetExePath)
$start.ArgumentList.Add('--update-helper')
$start.ArgumentList.Add($DescriptorPath)
$start.ArgumentList.Add($Nonce)
$helper = [System.Diagnostics.Process]::Start($start)
if ($null -eq $helper) { throw 'Nie uruchomiono helpera aktualizacji.' }
$helper.Id | Set-Content -LiteralPath $HelperPidPath -Encoding ascii

$ready = [System.Threading.EventWaitHandle]::OpenExisting($ReadyEventName)
try {
    if (-not $ready.WaitOne([TimeSpan]::FromMinutes(2))) {
        throw 'Helper nie zgłosił gotowości w ciągu dwóch minut.'
    }
}
finally {
    $ready.Dispose()
    $helper.Dispose()
}
