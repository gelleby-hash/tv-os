# Windows Firewall rule that lets the phone remote reach the TV launcher.
#
# Run in an elevated Windows PowerShell (right-click Start > Terminal (Admin)),
# on the PC that runs the launcher, from the folder holding this file. Look at
# the dry run first:
#   powershell -ExecutionPolicy Bypass -File .\phone-remote-firewall.ps1 -RemoteAddress 192.168.0.0/24,192.168.0.20 -WhatIf
#   powershell -ExecutionPolicy Bypass -File .\phone-remote-firewall.ps1 -RemoteAddress 192.168.0.0/24,192.168.0.20
#   powershell -ExecutionPolicy Bypass -File .\phone-remote-firewall.ps1 -Undo
# (Bypass applies to that one run only; no execution policy change needed.)
#
# What it does, and nothing else: one inbound allow rule named
# 'TvLauncher phone remote', for
#   - TCP on -Port (default 47800, the launcher's PhoneRemote port),
#   - the program -Program only (default: the mini PC's install,
#     C:\Program Files\TvLauncher\TvLauncher.exe; on the dev PC give the path of
#     the build you run),
#   - connections from -RemoteAddress only: addresses, ranges or CIDR networks,
#     such as the phone's network, 192.168.0.0/24, and the dev PC,
#     192.168.0.20. Several values may be given separated by commas. It is
#     required, and 'Any' is refused: the rule is never open to every address,
#   - every network profile, Public included. The mini PC's Wi-Fi/Ethernet is
#     Public, and a rule limited to Private never matches there.
#
# The launcher itself needs no administrator rights; only this rule does. The
# phone remote can only press the Rii remote's keys, so this is the whole of
# what the firewall lets in.
#
# -WhatIf  prints the rule and what would be removed, and changes nothing.
# -Undo    removes the rule (nothing else is touched). Needs no -RemoteAddress.
# Re-running is safe: the script removes its own rule (by name) and creates it
# again with the values given, so a changed port or address list simply replaces
# the old one. It then reads the rule back from Windows and stops with an error
# if what is stored does not match what was asked for. Last, it lists any
# inbound Block rule for the same program (a Block rule overrides the Allow
# rule) and leaves it in place: it only reads those rules, never changes them.
#
# Run it before the launcher's PhoneRemote setting is switched on. If the
# launcher listens first, Windows shows its firewall dialog for TvLauncher.exe,
# and Cancel there creates exactly such Block rules.
#
# Tested against a simulated firewall with test-phone-remote-firewall.ps1.

param(
    [ValidateRange(1, 65535)][int]$Port = 47800,
    [string]$Program = 'C:\Program Files\TvLauncher\TvLauncher.exe',
    [string[]]$RemoteAddress,
    [switch]$Undo,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$RuleName = 'TvLauncher phone remote'

function Test-Administrator {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    Write-Host 'Run this in an elevated (Admin) PowerShell window.'
    exit 1
}

# powershell -File passes "-RemoteAddress a,b" as one string "a,b", so split here.
$addresses = @($RemoteAddress | ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

if (-not $Undo) {
    if (-not $addresses) {
        Write-Host 'The -RemoteAddress parameter is missing. Give the addresses allowed to connect, for example:'
        Write-Host '  -RemoteAddress 192.168.0.0/24,192.168.0.20'
        exit 1
    }
    $wide = @($addresses | Where-Object { $_ -in 'Any', '*', '0.0.0.0/0', '0.0.0.0-255.255.255.255' })
    if ($wide) {
        Write-Host ("-RemoteAddress must be specific addresses or networks, not '{0}'. Nothing was changed." -f ($wide -join ','))
        exit 1
    }
    if (-not (Test-Path -LiteralPath $Program)) {
        Write-Host ("Warning: {0} does not exist on this PC (yet). The rule is still created for that path." -f $Program)
    }
}

$existing = Get-NetFirewallRule -Name $RuleName -ErrorAction SilentlyContinue

function Show-Existing {
    $port = ($existing | Get-NetFirewallPortFilter).LocalPort
    $prog = ($existing | Get-NetFirewallApplicationFilter).Program
    $from = (($existing | Get-NetFirewallAddressFilter).RemoteAddress) -join ', '
    "port {0}, program {1}, from {2}" -f $port, $prog, $from
}

# ---- undo ----

if ($Undo) {
    if (-not $existing) {
        Write-Host ("There is no rule named '{0}'; nothing to remove." -f $RuleName)
        Write-Host 'DONE'
        exit 0
    }
    if ($WhatIf) {
        Write-Host ("Would remove the rule '{0}' ({1}). Nothing was changed." -f $RuleName, (Show-Existing))
        exit 0
    }
    Write-Host ("Removing the existing rule '{0}' ({1})." -f $RuleName, (Show-Existing))
    Remove-NetFirewallRule -Name $RuleName
    if (Get-NetFirewallRule -Name $RuleName -ErrorAction SilentlyContinue) { throw "The rule '$RuleName' is still there after removing it." }
    Write-Host 'DONE'
    exit 0
}

# ---- apply ----

$verb = if ($WhatIf) { 'Would create' } else { 'Creating' }
Write-Host ("{0} the rule '{1}':" -f $verb, $RuleName)
Write-Host ("  inbound allow, TCP {0}, program {1}" -f $Port, $Program)
Write-Host ("  from {0}" -f ($addresses -join ', '))
Write-Host '  on every network profile (Domain, Private and Public)'
if ($WhatIf) {
    if ($existing) { Write-Host ("Would first remove the existing rule ({0})." -f (Show-Existing)) }
    Write-Host 'Nothing was changed. This was a dry run (-WhatIf).'
    exit 0
}

if ($existing) {
    Write-Host ("Removing the existing rule '{0}' ({1}) so it can be replaced." -f $RuleName, (Show-Existing))
    Remove-NetFirewallRule -Name $RuleName
}
New-NetFirewallRule -Name $RuleName -DisplayName $RuleName -Direction Inbound -Protocol TCP -LocalPort $Port `
    -Program $Program -RemoteAddress $addresses -Profile Any -Action Allow -Enabled True | Out-Null

# Read it back from Windows: trust the stored rule, not the success of the call.
$rule = Get-NetFirewallRule -Name $RuleName -ErrorAction SilentlyContinue
if (-not $rule) { throw "The rule '$RuleName' does not exist after creating it." }
$storedPort = ($rule | Get-NetFirewallPortFilter).LocalPort
$storedProg = ($rule | Get-NetFirewallApplicationFilter).Program
$storedFrom = @(($rule | Get-NetFirewallAddressFilter).RemoteAddress)
$wrong = @()
if ("$($rule.Enabled)" -ne 'True')                 { $wrong += "enabled is $($rule.Enabled)" }
if ("$($rule.Profile)" -ne 'Any')                  { $wrong += "profile is $($rule.Profile)" }
if ("$storedPort" -ne "$Port")                     { $wrong += "port is $storedPort" }
if ("$storedProg" -ine $Program)                   { $wrong += "program is $storedProg" }
if ($storedFrom.Count -ne $addresses.Count)        { $wrong += ("remote addresses are {0}" -f ($storedFrom -join ', ')) }
if ($wrong) { throw ("The rule was created but Windows stores something different from what was asked: {0}." -f ($wrong -join '; ')) }

Write-Host ''
Write-Host 'Stored by Windows:'
Write-Host ("  rule:     {0} (enabled, profile {1})" -f $rule.Name, $rule.Profile)
Write-Host ("  port:     TCP {0}" -f $storedPort)
Write-Host ("  program:  {0}" -f $storedProg)
Write-Host ("  from:     {0}" -f ($storedFrom -join ', '))

# An inbound Block rule for the same program wins over this Allow rule. Windows
# creates such rules itself (named "TCP Query User{...}" and "UDP Query
# User{...}") when its firewall dialog for a program is cancelled, which happens
# if the launcher listens before this rule exists. Only reported: removing a rule
# this script did not make is the owner's decision.
$blocks = @(Get-NetFirewallApplicationFilter |
    Where-Object { [Environment]::ExpandEnvironmentVariables("$($_.Program)") -ieq $Program } |
    Get-NetFirewallRule |
    Where-Object { "$($_.Direction)" -eq 'Inbound' -and "$($_.Action)" -eq 'Block' })
if ($blocks) {
    Write-Host ''
    Write-Host ("Warning: Windows also has {0} inbound Block rule(s) for {1}." -f $blocks.Count, $Program)
    Write-Host 'A Block rule overrides the Allow rule above, so the phone remote cannot connect while an enabled one is there:'
    foreach ($b in $blocks) {
        Write-Host ("  '{0}' (name {1}, profile {2}, enabled {3})" -f $b.DisplayName, $b.Name, $b.Profile, $b.Enabled)
    }
    Write-Host 'This script has not changed them. Removing them is the owner''s decision (Windows Defender Firewall'
    Write-Host 'with Advanced Security > Inbound Rules, or Remove-NetFirewallRule -Name <name> in an elevated window).'
}
Write-Host 'DONE'
exit 0
