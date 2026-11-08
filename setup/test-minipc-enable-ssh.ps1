# Tests minipc-enable-ssh.ps1's control flow on any PC, without admin rights and
# without changing the machine: every command that would change the system, or
# that needs elevation, is replaced by a fake. Aliases outrank functions and
# cmdlets in PowerShell, so the fakes win even over the script's own functions.
#
#   powershell -ExecutionPolicy Bypass -File .\test-minipc-enable-ssh.ps1
#
# Exits non-zero if any scenario does not behave as expected.

param([string]$Scenario)

$Target = Join-Path $PSScriptRoot 'minipc-enable-ssh.ps1'

$Scenarios = [ordered]@{
    # name              = expected exit code, text that must appear, text that must not appear
    'already-installed' = @{ Exit = 0; Must = @('DONE', 'remote 192.0.2.10');             MustNot = @('Installing it', 'winget install') }
    'builtin-works'     = @{ Exit = 0; Must = @('Installing it', 'DONE');                   MustNot = @('winget install') }
    'needs-restart'     = @{ Exit = 1; Must = @('restart', 'run this script again');        MustNot = @('winget install', 'DONE') }
    'builtin-throws'    = @{ Exit = 0; Must = @('built-in install failed', 'winget install', 'DONE'); MustNot = @() }
    'builtin-noop'      = @{ Exit = 0; Must = @('winget install', 'DONE');                  MustNot = @('restart the mini PC') }
    'winget-fails'      = @{ Exit = 1; Must = @('Neither install produced');                MustNot = @('DONE') }
    'not-admin'         = @{ Exit = 1; Must = @('elevated');                                MustNot = @('Installing it', 'DONE') }
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
        foreach ($t in $exp.Must)    { if ($out -notmatch [regex]::Escape($t)) { $problems += "missing '$t'" } }
        foreach ($t in $exp.MustNot) { if ($out -match    [regex]::Escape($t)) { $problems += "unexpected '$t'" } }
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
    Admin        = $Scenario -ne 'not-admin'
    Service      = $Scenario -eq 'already-installed'
    CapState     = 'NotPresent'
    Add          = @{ 'builtin-works' = 'ok'; 'needs-restart' = 'restart'; 'builtin-throws' = 'throw'
                      'builtin-noop' = 'noop'; 'winget-fails' = 'noop' }[$Scenario]
    WingetWorks  = $Scenario -ne 'winget-fails'
    FakeSshd     = Join-Path $env:TEMP 'tvos-fake-sshd.cmd'
}
Set-Content -Path $T.FakeSshd -Value '@exit /b 0' -Encoding ascii

function Fake-Admin { $T.Admin }
function Fake-GetService {
    if ($T.Service) { [pscustomobject]@{ Name = 'sshd'; Status = 'Running'; StartType = 'Automatic' } }
}
function Fake-GetCapability { [pscustomobject]@{ Name = 'OpenSSH.Server~~~~0.0.1.0'; State = $T.CapState } }
function Fake-AddCapability {
    switch ($T.Add) {
        'ok'      { $T.CapState = 'Installed'; $T.Service = $true; [pscustomobject]@{ RestartNeeded = $false } }
        'restart' { $T.CapState = 'InstallPending'; [pscustomobject]@{ RestartNeeded = $true } }
        'throw'   { throw 'Add-WindowsCapability failed. Error code = 0x800f0954' }
        'noop'    { [pscustomobject]@{ RestartNeeded = $false } }
    }
}
function Fake-Winget {
    Write-Host ("[fake] winget {0}" -f ($args -join ' '))
    if ($T.WingetWorks) { $T.Service = $true; $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 }
}
function Fake-Cim { [pscustomobject]@{ PathName = '"' + $T.FakeSshd + '"' } }
function Fake-GetContent { "# default sshd_config`r`nSubsystem sftp sftp-server.exe`r`n" }
function Fake-Icacls { $global:LASTEXITCODE = 0 }
function Fake-PortFilter {
    [pscustomobject]@{ LocalPort = '22'; Rule = 'OpenSSH-Server-In-TCP' }
    [pscustomobject]@{ LocalPort = '22'; Rule = 'OpenSSH SSH Server Preview (sshd)' }
    [pscustomobject]@{ LocalPort = '3389'; Rule = 'RemoteDesktop-In' }
}
function Fake-GetRule {
    $names = @($input | ForEach-Object { $_.Rule })
    if (-not $names) {
        # Called with -Name: report whether our named rule exists (it does).
        return [pscustomobject]@{ Name = 'OpenSSH-Server-In-TCP'; Direction = 'Inbound'; Action = 'Allow' }
    }
    $names | ForEach-Object { [pscustomobject]@{ Name = $_; Direction = 'Inbound'; Action = 'Allow' } }
}
function Fake-SetRule {
    $i = [array]::IndexOf($args, '-RemoteAddress')
    if ($i -ge 0) { Write-Host ("[fake] rule {0} -> remote {1}" -f $args[[array]::IndexOf($args, '-Name') + 1], $args[$i + 1]) }
}
function Fake-AddressFilter { [pscustomobject]@{ RemoteAddress = @('192.0.2.10') } }
function Fake-Nothing { }

Set-Alias Test-Administrator           Fake-Admin
Set-Alias Get-Service                  Fake-GetService
Set-Alias Start-Service                Fake-Nothing
Set-Alias Restart-Service              Fake-Nothing
Set-Alias Set-Service                  Fake-Nothing
Set-Alias Get-WindowsCapability        Fake-GetCapability
Set-Alias Add-WindowsCapability        Fake-AddCapability
Set-Alias winget                       Fake-Winget
Set-Alias Get-CimInstance              Fake-Cim
Set-Alias Set-Content                  Fake-Nothing
Set-Alias Get-Content                  Fake-GetContent
Set-Alias Copy-Item                    Fake-Nothing
Set-Alias icacls                       Fake-Icacls
Set-Alias Get-NetFirewallRule          Fake-GetRule
Set-Alias New-NetFirewallRule          Fake-Nothing
Set-Alias Set-NetFirewallRule          Fake-SetRule
Set-Alias Get-NetFirewallPortFilter    Fake-PortFilter
Set-Alias Get-NetFirewallAddressFilter Fake-AddressFilter
Set-Alias New-Item                     Fake-Nothing
Set-Alias New-ItemProperty             Fake-Nothing

# Called as its own script (not dot-sourced) so its exit codes behave as they do
# when run with -File; the aliases are still visible in its scope.
# A documentation address (RFC 5737) and a dummy key stand in for the dev PC's.
& $Target -DevPcIp '192.0.2.10' -PubKey 'ssh-ed25519 AAAATESTKEY test'
exit $LASTEXITCODE
