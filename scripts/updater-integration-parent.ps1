param(
    [Parameter(Mandatory = $true)][string]$ReadyEventName
)

$ErrorActionPreference = 'Stop'
$ready = [System.Threading.EventWaitHandle]::OpenExisting($ReadyEventName)
try {
    if (-not $ready.WaitOne([TimeSpan]::FromMinutes(3))) {
        throw 'Helper nie zgłosił gotowości w ciągu trzech minut.'
    }
}
finally {
    $ready.Dispose()
}
