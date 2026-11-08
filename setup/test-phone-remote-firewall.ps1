# Tests phone-remote-firewall.ps1's control flow on any PC, without admin rights
# and without changing the machine's firewall: every command that would change
# the system, or that needs elevation, is replaced by a fake. Aliases outrank
# functions and cmdlets in PowerShell, so the fakes win even over the script's
# own functions.
#
#   powershell -ExecutionPolicy Bypass -File .\test-phone-remote-firewall.ps1
#
# Exits non-zero if any scenario does not behave as expected.

param([string]$Scenario)

$Target = Join-Path $PSScriptRoot 'phone-remote-firewall.ps1'
$Exe = 'C:\Program Files\TvLauncher\TvLauncher.exe'

$Scenarios = [ordered]@{
    # name           = expected exit code, text that must appear, text that must not appear,
    #                  how many times a text must appear (Count), a pattern that must match (Order)
    'adds-rule'      = @{ Exit = 0
                          Must = @('-Direction Inbound', '-Protocol TCP', '-LocalPort 47800', "-Program $Exe",
                                   '-RemoteAddress 192.168.0.0/24,192.168.0.20', '-Profile Any', '-Action Allow', 'DONE')
                          MustNot = @('Remove-NetFirewallRule', 'FAILED', 'Block rule')
                          Count = @{ '[fake] New-NetFirewallRule' = 1 } }
    'replaces-rule'  = @{ Exit = 0
                          Must = @('Removing the existing rule', 'port 47801', '-LocalPort 47800', 'DONE')
                          MustNot = @('FAILED')
                          Count = @{ '[fake] Remove-NetFirewallRule' = 1; '[fake] New-NetFirewallRule' = 1 }
                          Order = '(?s)\[fake\] Remove-NetFirewallRule.*\[fake\] New-NetFirewallRule' }
    'undo'           = @{ Exit = 0
                          Must = @('Removing the existing rule', 'DONE')
                          MustNot = @('New-NetFirewallRule')
                          Count = @{ '[fake] Remove-NetFirewallRule' = 1 } }
    'undo-nothing'   = @{ Exit = 0
                          Must = @('no rule', 'DONE')
                          MustNot = @('Remove-NetFirewallRule', 'New-NetFirewallRule') }
    'whatif'         = @{ Exit = 0
                          Must = @('Would create', 'TCP 47800', $Exe, '192.168.0.0/24', '192.168.0.20', 'every network profile', 'Nothing was changed')
                          MustNot = @('[fake] New-NetFirewallRule', '[fake] Remove-NetFirewallRule', 'DONE') }
    'not-admin'      = @{ Exit = 1
                          Must = @('elevated')
                          MustNot = @('[fake] New-NetFirewallRule', '[fake] Remove-NetFirewallRule', 'DONE') }
    'no-addresses'   = @{ Exit = 1
                          Must = @('-RemoteAddress')
                          MustNot = @('[fake] New-NetFirewallRule', '[fake] Remove-NetFirewallRule', 'DONE') }
    'any-address'    = @{ Exit = 1
                          Must = @('-RemoteAddress', 'specific')
                          MustNot = @('[fake] New-NetFirewallRule', '[fake] Remove-NetFirewallRule', 'DONE') }
    # Block rules Windows made when its dialog was cancelled: reported, never removed.
    'block-rule-warns' = @{ Exit = 0
                          Must = @('2 inbound Block rule(s)', "'TvLauncher.exe' (name {BLOCK-TCP}", 'name {BLOCK-UDP}',
                                   'overrides the Allow rule', 'owner''s decision', 'DONE')
                          MustNot = @('[fake] Remove-NetFirewallRule', 'OTHER-PROGRAM', 'OUTBOUND-BLOCK', 'OWN-ALLOW')
                          Count = @{ '[fake] New-NetFirewallRule' = 1 }
                          Order = '(?s)\[fake\] New-NetFirewallRule.*Warning: Windows also has' }
}

if (-not $Scenario) {
    # Runner: each scenario in its own process, because the script calls exit.
    $failed = 0
    foreach ($name in $Scenarios.Keys) {
        $exp = $Scenarios[$name]
        $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Scenario $name 2>&1 | Out-String
        $code = $LASTEXITCODE
        $problems = @()
        if ($code -ne $exp.Exit) { $problems += "exit $code, expected $($exp.Exit)" }
        foreach ($t in $exp.Must)    { if (-not $out.Contains($t)) { $problems += "missing '$t'" } }
        foreach ($t in $exp.MustNot) { if ($out.Contains($t))      { $problems += "unexpected '$t'" } }
        if ($exp.Count) {
            foreach ($t in $exp.Count.Keys) {
                $n = ([regex]::Matches($out, [regex]::Escape($t))).Count
                if ($n -ne $exp.Count[$t]) { $problems += "'$t' appeared $n time(s), expected $($exp.Count[$t])" }
            }
        }
        if ($exp.Order -and $out -notmatch $exp.Order) { $problems += "wrong order: expected /$($exp.Order)/" }
        if ($problems) {
            $failed++
            Write-Host ("FAIL {0}: {1}" -f $name, ($problems -join '; '))
            Write-Host ($out -replace '(?m)^', '     | ')
        } else {
            Write-Host ("pass {0}" -f $name)
        }
    }
    Write-Host ("{0} of {1} scenarios passed" -f ($Scenarios.Count - $failed), $Scenarios.Count)
    exit [int]($failed -gt 0)
}

# ---- one scenario: fakes, then run the real script in this scope ----

$global:T = @{
    Admin = $Scenario -ne 'not-admin'
    Rule  = $null     # the rule "in the firewall": Name, Enabled, Profile, Program, Port, Remote
    Others = @()      # other rules: Name, DisplayName, Direction, Action, Enabled, Profile, Program
}
if ($Scenario -eq 'block-rule-warns') {
    # What a cancelled firewall dialog leaves (TCP and UDP, one with the path in
    # another case), plus rules that must not be reported.
    $T.Others = @(
        @{ Name = '{BLOCK-TCP}'; DisplayName = 'TvLauncher.exe'; Direction = 'Inbound'; Action = 'Block'; Enabled = 'True'; Profile = 'Private'; Program = $Exe.ToLower() }
        @{ Name = '{BLOCK-UDP}'; DisplayName = 'TvLauncher.exe'; Direction = 'Inbound'; Action = 'Block'; Enabled = 'True'; Profile = 'Private'; Program = $Exe }
        @{ Name = '{OTHER-PROGRAM}'; DisplayName = 'testhost'; Direction = 'Inbound'; Action = 'Block'; Enabled = 'True'; Profile = 'Private'; Program = 'C:\x\testhost.exe' }
        @{ Name = '{OUTBOUND-BLOCK}'; DisplayName = 'out'; Direction = 'Outbound'; Action = 'Block'; Enabled = 'True'; Profile = 'Any'; Program = $Exe }
        @{ Name = '{OWN-ALLOW}'; DisplayName = 'allow'; Direction = 'Inbound'; Action = 'Allow'; Enabled = 'True'; Profile = 'Any'; Program = $Exe }
    )
}
if ($Scenario -in 'replaces-rule', 'undo') {
    # An older copy of our rule is already there (replaces-rule: on another port).
    $T.Rule = @{ Name = 'TvLauncher phone remote'; Enabled = 'True'; Profile = 'Any'; Program = $Exe
                 Port = $(if ($Scenario -eq 'replaces-rule') { '47801' } else { '47800' }); Remote = @('192.168.0.20') }
}

function Fake-Admin { $T.Admin }
function Fake-RuleObject($r) {
    [pscustomobject]@{ Name = $r.Name; DisplayName = $(if ($r.DisplayName) { $r.DisplayName } else { $r.Name })
                       Direction = $(if ($r.Direction) { $r.Direction } else { 'Inbound' })
                       Action = $(if ($r.Action) { $r.Action } else { 'Allow' })
                       Enabled = $r.Enabled; Profile = $r.Profile }
}
function Fake-GetRule {
    # Piped application filters: the rule each belongs to. Nothing piped: our
    # rule (the script asks for it by name only).
    $in = @($input | Where-Object { $_ })
    if ($in) {
        foreach ($f in $in) { @($T.Rule) + $T.Others | Where-Object { $_ -and $_.Name -eq $f.RuleName } | ForEach-Object { Fake-RuleObject $_ } }
        return
    }
    if ($T.Rule) { Fake-RuleObject $T.Rule }
}
function Fake-RemoveRule {
    Write-Host ("[fake] Remove-NetFirewallRule {0}" -f ($args -join ' '))
    $T.Rule = $null
}
function Fake-NewRule {
    # Records what the script asked for, in the form it would be read back.
    $a = @{}
    for ($i = 0; $i -lt $args.Count; $i += 2) { $a[[string]$args[$i]] = $args[$i + 1] }
    $shown = for ($i = 0; $i -lt $args.Count; $i += 2) { '{0} {1}' -f $args[$i], (@($args[$i + 1]) -join ',') }
    Write-Host ("[fake] New-NetFirewallRule {0}" -f ($shown -join ' '))
    $T.Rule = @{ Name = $a['-Name']; Enabled = 'True'; Profile = [string]$a['-Profile']; Program = $a['-Program']
                 Port = [string]$a['-LocalPort']; Remote = @($a['-RemoteAddress']) }
}
function Fake-AppFilter {
    # A piped rule: its filter. Nothing piped: every rule's filter in the firewall.
    $in = @($input | Where-Object { $_ })
    if ($in) { return [pscustomobject]@{ Program = $T.Rule.Program; RuleName = $T.Rule.Name } }
    @($T.Rule) + $T.Others | Where-Object { $_ } | ForEach-Object { [pscustomobject]@{ Program = $_.Program; RuleName = $_.Name } }
}
function Fake-PortFilter    { [pscustomobject]@{ LocalPort = $T.Rule.Port; Protocol = 'TCP' } }
function Fake-AddressFilter { [pscustomobject]@{ RemoteAddress = $T.Rule.Remote } }

Set-Alias Test-Administrator             Fake-Admin
Set-Alias Get-NetFirewallRule            Fake-GetRule
Set-Alias Remove-NetFirewallRule         Fake-RemoveRule
Set-Alias New-NetFirewallRule            Fake-NewRule
Set-Alias Get-NetFirewallApplicationFilter Fake-AppFilter
Set-Alias Get-NetFirewallPortFilter      Fake-PortFilter
Set-Alias Get-NetFirewallAddressFilter   Fake-AddressFilter

# What each scenario passes to the script. adds-rule gives the addresses as one
# comma-separated string, which is what powershell -File hands over for
# "-RemoteAddress a,b"; the others give a real array.
$params = switch ($Scenario) {
    'adds-rule'    { @{ RemoteAddress = '192.168.0.0/24,192.168.0.20'; Program = $Exe } }
    'replaces-rule'{ @{ RemoteAddress = @('192.168.0.0/24', '192.168.0.20'); Program = $Exe } }
    'undo'         { @{ Undo = $true; Program = $Exe } }
    'undo-nothing' { @{ Undo = $true; Program = $Exe } }
    'whatif'       { @{ RemoteAddress = @('192.168.0.0/24', '192.168.0.20'); Program = $Exe; WhatIf = $true } }
    'not-admin'    { @{ RemoteAddress = @('192.168.0.20'); Program = $Exe } }
    'no-addresses' { @{ Program = $Exe } }
    'any-address'  { @{ RemoteAddress = @('Any'); Program = $Exe } }
    'block-rule-warns' { @{ RemoteAddress = @('192.168.0.0/24'); Program = $Exe } }
}

# Called as its own script (not dot-sourced) so its exit codes behave as they do
# when run with -File; the aliases are still visible in its scope.
& $Target @params
exit $LASTEXITCODE
