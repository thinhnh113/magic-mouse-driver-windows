# Removes the MagicMouse filter driver (MagicTouchFilter) so the mouse falls back to the stock Windows driver.
# Run elevated. Exit codes: 0 = done, 3010 = done but a reboot is required.
param([string]$LogFile)
$ErrorActionPreference = 'Continue'

function Log([string]$Message) {
    $line = "$(Get-Date -Format s) $Message"
    Write-Output $line
    if ($LogFile) { Add-Content -Path $LogFile -Value $line -Encoding UTF8 }
}

$reboot = $false

# Every staged copy of our package (each build has its own oemNN.inf). Get-WindowsDriver is
# locale-independent, unlike parsing pnputil's text output.
$packages = Get-WindowsDriver -Online -All -ErrorAction SilentlyContinue |
    Where-Object { $_.OriginalFileName -like '*\magictouchfilter.inf' }
foreach ($p in $packages) {
    # /uninstall removes the driver from devices using it; they are re-installed with the stock driver.
    $out = pnputil /delete-driver $p.Driver /uninstall /force 2>&1
    Log "delete $($p.Driver) ($($p.Version)): exit $LASTEXITCODE $($out -join ' ')"
    if ($LASTEXITCODE -eq 3010) { $reboot = $true }
}

pnputil /scan-devices | Out-Null

# Service entry left behind by the driver package.
if (Get-Service -Name MagicTouchFilter -ErrorAction SilentlyContinue) {
    sc.exe delete MagicTouchFilter | Out-Null
    Log 'Deleted service MagicTouchFilter'
}

# Test certificate trusted by install-driver.ps1.
foreach ($store in 'Root', 'TrustedPublisher') {
    Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object Subject -eq 'CN=MagicTouch Test' | ForEach-Object {
        Remove-Item $_.PSPath
        Log "Removed test certificate from $store"
    }
}

if ($reboot) { Log 'Reboot required'; exit 3010 }
Log 'Driver removed'
exit 0
