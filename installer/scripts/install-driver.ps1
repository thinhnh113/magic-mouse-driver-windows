# Installs the MagicMouse filter driver (MagicTouchFilter) and attaches it to connected Magic Mice.
# Run elevated. Exit codes: 0 = done, 3010 = done but a reboot is required, anything else = failure.
param(
    [Parameter(Mandatory = $true)][string]$DriverDir,
    [string]$LogFile
)
$ErrorActionPreference = 'Stop'

function Log([string]$Message) {
    $line = "$(Get-Date -Format s) $Message"
    Write-Output $line
    if ($LogFile) { Add-Content -Path $LogFile -Value $line -Encoding UTF8 }
}

$inf = Join-Path $DriverDir 'MagicTouchFilter.inf'
$cer = Join-Path $DriverDir 'MagicTouchTest.cer'
$reboot = $false

try {
    # A test-signed build ships its certificate; Windows only loads it when the certificate is trusted
    # (and test-signing mode is on). A Microsoft-signed build ships without the .cer.
    if (Test-Path $cer) {
        certutil -f -addstore Root $cer | Out-Null
        certutil -f -addstore TrustedPublisher $cer | Out-Null
        Log "Trusted test certificate $cer"
    }

    $out = pnputil /add-driver $inf /install 2>&1
    $code = $LASTEXITCODE
    Log "pnputil /add-driver exit $code`r`n$($out -join "`r`n")"
    # 259 = package staged but no connected device needed updating (e.g. the mouse is off).
    if ($code -eq 3010) { $reboot = $true }
    elseif ($code -ne 0 -and $code -ne 259) { exit $code }

    # Restart connected Magic Mice so the filter attaches right away.
    $pattern = '^(BTHENUM\\\{00001124-0000-1000-8000-00805F9B34FB\}_VID&(000205AC|0001004C)_PID&(030D|0310|0269|0323)\\|USB\\VID_05AC&PID_(0269|0323)&MI_01\\)'
    foreach ($d in Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match $pattern }) {
        $r = pnputil /restart-device "$($d.InstanceId)" 2>&1
        Log "restart $($d.InstanceId): exit $LASTEXITCODE $($r -join ' ')"
        if ($LASTEXITCODE -eq 3010) { $reboot = $true }
    }
}
catch {
    Log "ERROR: $_"
    exit 1
}

if ($reboot) { Log 'Reboot required'; exit 3010 }
Log 'Driver installed'
exit 0
