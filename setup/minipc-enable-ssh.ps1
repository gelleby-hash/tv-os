# Enable key-only SSH on the mini PC, reachable from the dev PC only.
#
# Run ON THE MINI PC in an elevated Windows PowerShell (right-click Start >
# Terminal (Admin)), signed in as the admin account the dev PC should use, from the
# folder holding this file:
#   powershell -ExecutionPolicy Bypass -File .\minipc-enable-ssh.ps1 -DevPcIp <dev PC address> -PubKey "<contents of id_ed25519_tvbox.pub>"
# (Bypass applies to that one run only; no execution policy change needed.)
# Safe to re-run. Tested against simulated cases with test-minipc-enable-ssh.ps1.
#
# What it does, and nothing else:
#   1. installs OpenSSH Server: Windows' built-in optional feature, or if Windows
#      Update can't deliver it, Microsoft's own package of it via winget
#   2. authorises exactly one public key (the dev PC's id_ed25519_tvbox)
#   3. turns password and keyboard-interactive login off (keys only)
#   4. limits every inbound firewall rule for port 22 to the dev PC's IP
#   5. makes PowerShell the SSH shell and starts sshd automatically at boot
#
# Undo: Stop-Service sshd; Set-Service sshd -StartupType Disabled
#       (or remove "OpenSSH Server" in Settings > Optional features, or
#       "OpenSSH" in Installed apps if winget installed it).

param(
    # The dev PC's IPv4 address: the only address the firewall lets reach port 22.
    [Parameter(Mandatory)][ValidateScript({ [Net.IPAddress]::TryParse($_, [ref]$null) })][string]$DevPcIp,
    # The dev PC's public key, the whole line of id_ed25519_tvbox.pub.
    [Parameter(Mandatory)][ValidatePattern('^ssh-ed25519 ')][string]$PubKey
)

$ErrorActionPreference = 'Stop'

function Test-Administrator {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Administrator)) { throw 'Run this in an elevated (Admin) PowerShell window.' }

# 1. OpenSSH Server
$restartNeeded = $false
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
    Write-Host ("Built-in OpenSSH Server ({0}): {1}" -f $cap.Name, $cap.State)
    if ($cap.State -ne 'Installed') {
        Write-Host 'Installing it through Windows Update (can take a few minutes)...'
        try {
            $restartNeeded = [bool](Add-WindowsCapability -Online -Name $cap.Name).RestartNeeded
        } catch {
            Write-Host ("  built-in install failed: {0}" -f $_.Exception.Message)
        }
        Write-Host ("  state now: {0}, restart needed: {1}" -f (Get-WindowsCapability -Online -Name $cap.Name).State, $restartNeeded)
    }
}
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    if ($restartNeeded) {
        Write-Host ''
        Write-Host 'Windows needs a restart to finish installing OpenSSH Server.'
        Write-Host 'Restart the mini PC, then run this script again.'
        exit 1
    }
    Write-Host 'No sshd service from the built-in feature; installing Microsoft.OpenSSH.Preview with winget...'
    winget install --id Microsoft.OpenSSH.Preview --exact --source winget --silent --accept-package-agreements --accept-source-agreements
    Write-Host ("  winget exit code: {0}" -f $LASTEXITCODE)
    if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
        throw 'Neither install produced an sshd service. Copy this whole window to the dev PC.'
    }
}
# The first start generates host keys and the default sshd_config.
Start-Service sshd
$sshdExe = (Get-CimInstance Win32_Service -Filter "Name='sshd'").PathName.Trim('"')
Write-Host ("sshd installed: {0}" -f $sshdExe)

# 2. The one authorised key. Accounts in Administrators read this file, not
#    ~\.ssh\authorized_keys. ACL: Administrators + SYSTEM only (sshd refuses
#    the file otherwise). SIDs so it works on any Windows language.
$sshDir = Join-Path $env:ProgramData 'ssh'
$akf = Join-Path $sshDir 'administrators_authorized_keys'
Set-Content -Path $akf -Value $PubKey -Encoding ascii
icacls $akf /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls could not set permissions on $akf" }

# 3. Keys only. sshd uses the first value it reads, so the block goes at the top.
$cfg = Join-Path $sshDir 'sshd_config'
$marker = '# tv-os: key-only login'
$current = Get-Content $cfg -Raw
if ($current -notmatch [regex]::Escape($marker)) {
    $block = @(
        $marker,
        'PubkeyAuthentication yes',
        'PasswordAuthentication no',
        'KbdInteractiveAuthentication no',
        'PermitEmptyPasswords no',
        ''
    ) -join "`r`n"
    Copy-Item $cfg "$cfg.bak-tvos" -Force
    Set-Content -Path $cfg -Value ($block + $current) -Encoding ascii -NoNewline
}
& $sshdExe -t
if ($LASTEXITCODE -ne 0) { throw "sshd_config failed validation; the original is at $cfg.bak-tvos" }

# 4. Firewall: port 22 from the dev PC only, whatever the network profile. The
#    winget package may add its own rule, so every inbound allow rule on 22 is
#    restricted, not just ours.
$ruleName = 'OpenSSH-Server-In-TCP'
if (-not (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name $ruleName -DisplayName 'OpenSSH Server (sshd)' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow | Out-Null
}
Set-NetFirewallRule -Name $ruleName -Enabled True
$sshRules = @(Get-NetFirewallPortFilter -Protocol TCP | Where-Object { $_.LocalPort -contains '22' } |
    Get-NetFirewallRule | Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' })
foreach ($r in $sshRules) { Set-NetFirewallRule -Name $r.Name -RemoteAddress $DevPcIp -Profile Any }

# 5. PowerShell as the SSH shell; sshd on at boot.
New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -PropertyType String -Force `
    -Value (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') | Out-Null
Set-Service sshd -StartupType Automatic
Restart-Service sshd

# Report what the dev PC needs to know.
Write-Host ''
Write-Host '--- copy everything below to the dev PC ---'
Write-Host ("SSH user:        {0}" -f $env:USERNAME)
Write-Host ("whoami:          {0}" -f (whoami))
Write-Host ("sshd:            {0}, {1}" -f (Get-Service sshd).Status, (Get-Service sshd).StartType)
foreach ($r in $sshRules) {
    $remote = ($r | Get-NetFirewallAddressFilter).RemoteAddress -join ','
    Write-Host ("firewall rule:   {0} -> remote {1}" -f $r.Name, $remote)
}
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    ForEach-Object { Write-Host ("IPv4:            {0}/{1} ({2})" -f $_.IPAddress, $_.PrefixLength, $_.InterfaceAlias) }
Get-NetConnectionProfile | ForEach-Object { Write-Host ("network profile: {0} = {1}" -f $_.InterfaceAlias, $_.NetworkCategory) }
Write-Host 'DONE'
