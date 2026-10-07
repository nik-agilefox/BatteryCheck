# Temporarily stop or start ONE Windows service (for "who wakes the GPU" checks). Run elevated via UAC:
#   Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File tools\service.ps1 -Action stop -Name AcerSysMonitorService'
# Only stops/starts — never changes the startup type: after a reboot the service starts as usual.
# The result is written to %TEMP%\bc_service.txt so a non-elevated process can read it.
param(
    [ValidateSet("stop", "start", "status")] [string]$Action = "status",
    [Parameter(Mandatory = $true)] [string]$Name
)
$out = Join-Path $env:TEMP "bc_service.txt"
try {
    $s = Get-Service -Name $Name -ErrorAction Stop
    switch ($Action) {
        "stop"  { Stop-Service -Name $Name -Force -ErrorAction Stop; $s.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20)) }
        "start" { Start-Service -Name $Name -ErrorAction Stop; $s.WaitForStatus("Running", [TimeSpan]::FromSeconds(20)) }
    }
    $s.Refresh()
    "{0:yyyy-MM-dd HH:mm:ss} {1} {2}: {3} (startup {4})" -f (Get-Date), $Action, $Name, $s.Status, $s.StartType | Out-File $out -Encoding utf8
}
catch {
    "{0:yyyy-MM-dd HH:mm:ss} {1} {2}: ERROR {3}" -f (Get-Date), $Action, $Name, $_.Exception.Message | Out-File $out -Encoding utf8
}
