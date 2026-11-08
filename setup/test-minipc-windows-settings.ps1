# Tests minipc-windows-settings.ps1's control flow on any PC, without admin rights
# and without changing the machine: the registry, the admin check and the account
# lookup are replaced by in-memory fakes. Aliases outrank functions and cmdlets
# in PowerShell, so the fakes win even over the script's own functions. The
# backup file is real, but lives in %TEMP% (the script's -StateFile parameter).
#
#   powershell -ExecutionPolicy Bypass -File .\test-minipc-windows-settings.ps1
#
# Exits non-zero if any scenario does not behave as expected.

param([string]$Scenario)

$Target = Join-Path $PSScriptRoot 'minipc-windows-settings.ps1'

$Scenarios = [ordered]@{
    # name = expected exit code, text that must appear, text that must not appear
    'fresh' = @{ Exit = 0
        Must = @('Applied', '[harness] all-applied=yes', '[harness] previous-recorded=yes', '[harness] state=exists',
                 '[harness] writes=15', '[harness] keycreates=5', '[harness] unrelated-edge-value-kept=yes',
                 '[harness] signin-or-sync-of-existing-profiles-touched=no')
        MustNot = @('Nothing to change', 'FAILED') }
    'already-applied' = @{ Exit = 0
        Must = @('Nothing to change', '[harness] second-run-writes=0', '[harness] second-run-keycreates=0', '[harness] backup-unchanged=yes')
        MustNot = @('FAILED') }
    'undo' = @{ Exit = 0
        Must = @('[harness] changed-by-apply=yes', '[harness] registry-restored=yes', '[harness] state-after-undo=absent',
                 '[harness] keyremoves=5', '[harness] second-undo-exit=0', 'Nothing to undo')
        MustNot = @('FAILED') }
    # The preview must say what a real undo does: the keys the script created end up empty and are removed.
    'undo-whatif' = @{ Exit = 0
        Must = @('[harness] undo-whatif-writes=0', '[harness] undo-whatif-removes=0', '[harness] undo-whatif-keyremoves=0',
                 '[harness] registry-unchanged=yes', '[harness] state-kept=yes', 'will be set back to',
                 '[harness] keys-previewed-as-removed=5')
        MustNot = @('FAILED', 'left in place') }
    'not-admin' = @{ Exit = 1
        Must = @('elevated', '[harness] writes=0')
        MustNot = @('Applied', 'Setting 1 of') }
    'wrong-machine' = @{ Exit = 1
        Must = @('NUCBOX_M6ULTRA', '[harness] writes=0')
        MustNot = @('Applied', 'Setting 1 of') }
    'whatif' = @{ Exit = 0
        Must = @('Setting 1 of 7', 'Setting 2 of 7', 'Setting 3 of 7', 'Setting 4 of 7', 'Setting 5 of 7', 'Setting 6 of 7', 'Setting 7 of 7',
                 'currently', 'will be set to', 'Restore pages', 'first-run', 'active hours', 'Nothing was written',
                 '[harness] writes=0', '[harness] removes=0', '[harness] keycreates=0', '[harness] state=absent',
                 '[harness] registry-unchanged=yes')
        MustNot = @('Applied', 'FAILED') }
    'never-sets-a-password' = @{ Exit = 0
        Must = @('[harness] nonempty-password-writes=0', '[harness] forbidden-calls=0',
                 '[harness] empty-password-written=yes', '[harness] static-scan=clean')
        MustNot = @('[fake] FORBIDDEN', 'FAILED') }
    'existing-default-password' = @{ Exit = 1
        Must = @('DefaultPassword', 'not touching', '[harness] writes=0', '[harness] state=absent')
        MustNot = @('hunter2', 'Applied') }
    'hive-not-loaded' = @{ Exit = 1
        Must = @('not loaded', 'Mini pc', 'FAILED', '[harness] others-applied=yes', '[harness] toast-written=no',
                 '[harness] later-run-exit=0', '[harness] later-run-writes=1', '[harness] later-run-completed=yes')
        MustNot = @('Nothing to change') }
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

# ---- one scenario: fakes, then run the real script ----

$env:COMPUTERNAME = if ($Scenario -eq 'wrong-machine') { 'SOME-OTHER-PC' } else { 'NUCBOX_M6ULTRA' }
$StateFile = Join-Path $env:TEMP ("tvos-winsettings-{0}-{1}.json" -f $Scenario, $PID)
Remove-Item -LiteralPath $StateFile -ErrorAction SilentlyContinue

$MiniSid = 'S-1-5-21-1111111111-2222222222-3333333333-1001'
$global:Cfg = @{ Admin = $Scenario -ne 'not-admin'; Sid = $MiniSid }
$global:RegVals = @{}                                                  # 'normalised key|name' -> @{ Value; Kind }
$global:RegKeys = New-Object 'System.Collections.Generic.HashSet[string]'
$global:Cnt = @{ Writes = 0; Removes = 0; KeyCreates = 0; KeyRemoves = 0; BadPw = 0; EmptyPw = 0; Forbidden = 0 }

function Norm([string]$p) {
    $p = $p.ToLower()
    $p = $p -replace '^hklm:\\?', 'hklm\'
    $p = $p -replace '^registry::hkey_local_machine\\', 'hklm\'
    $p = $p -replace '^registry::hkey_users\\', 'hku\'
    $p.TrimEnd('\')
}
function IsReg([string]$p) { $p -match '(?i)^(hklm:|registry::hkey_)' }
function KeyExists([string]$p) {
    $n = Norm $p
    $n -in @('hklm', 'hklm\software', 'hku') -or $RegKeys.Contains($n)
}
function Seed-Key([string]$p) {
    $n = Norm $p
    while ($n -match '\\') { [void]$RegKeys.Add($n); $n = $n.Substring(0, $n.LastIndexOf('\')) }
}
function Seed-Value([string]$key, [string]$name, $value, [string]$kind) {
    Seed-Key $key
    $RegVals[(Norm $key) + '|' + $name.ToLower()] = @{ Name = $name; Value = $value; Kind = $kind }
}
function Snapshot {
    $v = $RegVals.GetEnumerator() | Sort-Object Name | ForEach-Object { "{0}={1}:{2}" -f $_.Name, $_.Value.Kind, $_.Value.Value }
    $k = $RegKeys | Sort-Object
    (@($v) + @($k)) -join "`n"
}
function Fact([string]$name, $value) { Write-Host ("[harness] {0}={1}" -f $name, $value) }
function YesNo($b) { if ($b) { 'yes' } else { 'no' } }

function Fake-Admin { $Cfg.Admin }
function Fake-TestPath {
    [CmdletBinding()] param([string]$LiteralPath, [string]$Path)
    $p = if ($LiteralPath) { $LiteralPath } else { $Path }
    if (IsReg $p) { KeyExists $p } else { Microsoft.PowerShell.Management\Test-Path @PSBoundParameters }
}
function Fake-GetItemProperty {
    [CmdletBinding()] param([string]$LiteralPath, [string]$Path)
    $n = Norm $LiteralPath
    if (-not (KeyExists $LiteralPath)) { Write-Error "Cannot find path '$LiteralPath'"; return }
    $o = [ordered]@{}
    foreach ($e in $RegVals.GetEnumerator()) { if ($e.Name.StartsWith($n + '|')) { $o[$e.Value.Name] = $e.Value.Value } }
    $o['PSPath'] = $LiteralPath
    [pscustomobject]$o
}
function Fake-GetItem {
    [CmdletBinding()] param([string]$LiteralPath, [string]$Path)
    $p = if ($LiteralPath) { $LiteralPath } else { $Path }
    $n = Norm $p
    if (-not (KeyExists $p)) { Write-Error "Cannot find path '$p'"; return }
    $sub = @($RegKeys | Where-Object { $_.StartsWith($n + '\') -and -not $_.Substring($n.Length + 1).Contains('\') }).Count
    $vals = @($RegVals.Keys | Where-Object { $_.StartsWith($n + '|') }).Count
    $o = [pscustomobject]@{ SubKeyCount = $sub; ValueCount = $vals; Key = $n }
    $o | Add-Member -MemberType ScriptMethod -Name GetValueKind -Value {
        param($name) $RegVals[$this.Key + '|' + $name.ToLower()].Kind
    }
    $o
}
function Fake-NewItem {
    [CmdletBinding()] param([string]$Path, [string]$ItemType, [switch]$Force)
    if (-not (IsReg $Path)) { Microsoft.PowerShell.Management\New-Item @PSBoundParameters; return }
    $n = Norm $Path
    $parts = @(); $walk = $n
    while ($walk -match '\\' -and -not (KeyExists $walk)) { $parts += $walk; $walk = $walk.Substring(0, $walk.LastIndexOf('\')) }
    foreach ($q in $parts) { [void]$RegKeys.Add($q); $Cnt.KeyCreates++; Write-Host "[fake] create key $q" }
}
function Fake-NewItemProperty {
    [CmdletBinding()] param([string]$LiteralPath, [string]$Path, [string]$Name, [string]$PropertyType, $Value, [switch]$Force)
    if (-not (KeyExists $LiteralPath)) { throw "Cannot find path '$LiteralPath'" }
    $stored = if ($PropertyType -eq 'DWord') { [int]$Value } else { [string]$Value }
    $RegVals[(Norm $LiteralPath) + '|' + $Name.ToLower()] = @{ Name = $Name; Value = $stored; Kind = $PropertyType }
    $Cnt.Writes++
    if ($Name -eq 'DefaultPassword') { if ("$Value" -eq '') { $Cnt.EmptyPw++ } else { $Cnt.BadPw++ } }
    Write-Host ("[fake] write {0} {1} = {2} ({3})" -f (Norm $LiteralPath), $Name, $Value, $PropertyType)
}
function Fake-RemoveItemProperty {
    [CmdletBinding()] param([string]$LiteralPath, [string]$Name)
    $key = (Norm $LiteralPath) + '|' + $Name.ToLower()
    if (-not $RegVals.ContainsKey($key)) { throw "Property $Name does not exist at '$LiteralPath'" }
    $RegVals.Remove($key); $Cnt.Removes++
    Write-Host ("[fake] remove value {0} {1}" -f (Norm $LiteralPath), $Name)
}
function Fake-RemoveItem {
    [CmdletBinding()] param([string]$LiteralPath, [string]$Path, [switch]$Force, [switch]$Recurse)
    $p = if ($LiteralPath) { $LiteralPath } else { $Path }
    if (-not (IsReg $p)) { Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters; return }
    $i = Fake-GetItem -LiteralPath $p
    if (-not $i) { throw "Cannot find path '$p'" }
    if ($i.SubKeyCount -or $i.ValueCount) { throw "Key '$p' is not empty" }
    [void]$RegKeys.Remove((Norm $p)); $Cnt.KeyRemoves++
    Write-Host ("[fake] remove key {0}" -f (Norm $p))
}
function Fake-Cim {
    [CmdletBinding()] param([string]$ClassName, [string]$Filter)
    if ($Filter -match "Name='Mini pc'" -and $Cfg.Sid) { [pscustomobject]@{ Name = 'Mini pc'; SID = $Cfg.Sid } }
}
function Fake-Forbidden { $Cnt.Forbidden++; Write-Host ("[fake] FORBIDDEN call: {0}" -f ($args -join ' ')) }

Set-Alias Test-Administrator    Fake-Admin
Set-Alias Test-Path             Fake-TestPath
Set-Alias Get-ItemProperty      Fake-GetItemProperty
Set-Alias Get-Item              Fake-GetItem
Set-Alias New-Item              Fake-NewItem
Set-Alias New-ItemProperty      Fake-NewItemProperty
Set-Alias Set-ItemProperty      Fake-NewItemProperty
Set-Alias Remove-ItemProperty   Fake-RemoveItemProperty
Set-Alias Remove-Item           Fake-RemoveItem
Set-Alias Get-CimInstance       Fake-Cim
Set-Alias net                   Fake-Forbidden
Set-Alias net.exe               Fake-Forbidden
Set-Alias Set-LocalUser         Fake-Forbidden
Set-Alias New-LocalUser         Fake-Forbidden

$WL   = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$EDGE = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
$PUSH = "Registry::HKEY_USERS\$MiniSid\Software\Microsoft\Windows\CurrentVersion\PushNotifications"

# The machine as it is before the script: auto sign-in off, Edge policy key with
# one setting we change and one we must leave alone, none of the other keys.
function Seed-Machine([bool]$HiveLoaded = $true) {
    Seed-Value $WL 'AutoAdminLogon' '0' 'String'
    Seed-Value $EDGE 'StartupBoostEnabled' 1 'DWord'
    Seed-Value $EDGE 'HomepageLocation' 'https://example.test/' 'String'
    Seed-Key 'HKLM:\SOFTWARE\Policies\Microsoft\Windows'
    if ($HiveLoaded) { Seed-Key "Registry::HKEY_USERS\$MiniSid\Software\Microsoft\Windows\CurrentVersion" }
}
function Reset-Counters { foreach ($k in @($Cnt.Keys)) { $Cnt[$k] = 0 } }
function Invoke-Target([switch]$Undo, [switch]$WhatIf) {
    & $Target -StateFile $StateFile -Undo:$Undo -WhatIf:$WhatIf
    $LASTEXITCODE
}
function Get-Val($key, $name) { $RegVals[(Norm $key) + '|' + $name.ToLower()] }
function Test-AllApplied {
    $want = @(
        @($WL, 'AutoAdminLogon', '1', 'String'), @($WL, 'DefaultUserName', 'Mini pc', 'String'),
        @($WL, 'DefaultDomainName', 'NUCBOX_M6ULTRA', 'String'), @($WL, 'DefaultPassword', '', 'String'),
        @("$WL\SpecialAccounts\UserList", 'tvadmin', 0, 'DWord'), @($PUSH, 'ToastEnabled', 0, 'DWord'),
        @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU', 'NoAutoRebootWithLoggedOnUsers', 1, 'DWord'),
        @($EDGE, 'StartupBoostEnabled', 0, 'DWord'), @($EDGE, 'BackgroundModeEnabled', 0, 'DWord'),
        @($EDGE, 'HideRestoreDialogEnabled', 1, 'DWord'),
        @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate', 'SetActiveHours', 1, 'DWord'),
        @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate', 'ActiveHoursStart', 6, 'DWord'),
        @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate', 'ActiveHoursEnd', 0, 'DWord'),
        @($EDGE, 'HideFirstRunExperience', 1, 'DWord'), @($EDGE, 'ImplicitSignInEnabled', 0, 'DWord'))
    foreach ($w in $want) {
        $g = Get-Val $w[0] $w[1]
        if (-not $g -or "$($g.Value)" -ne "$($w[2])" -or $g.Kind -ne $w[3]) { return $false }
    }
    $true
}
function Get-StateEntries { if (Test-Path -LiteralPath $StateFile) { @((Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json).Entries) } else { @() } }

$exit = 0
switch ($Scenario) {
    'fresh' {
        Seed-Machine
        $exit = Invoke-Target
        Fact 'writes' $Cnt.Writes
        Fact 'keycreates' $Cnt.KeyCreates
        Fact 'state' $(if (Test-Path -LiteralPath $StateFile) { 'exists' } else { 'absent' })
        $e = Get-StateEntries
        $a = $e | Where-Object { $_.Name -eq 'AutoAdminLogon' }
        $s = $e | Where-Object { $_.Name -eq 'StartupBoostEnabled' }
        $h = $e | Where-Object { $_.Name -eq 'HideRestoreDialogEnabled' }
        Fact 'previous-recorded' (YesNo ($a.Existed -and "$($a.Value)" -eq '0' -and $s.Existed -and "$($s.Value)" -eq '1' -and $s.Kind -eq 'DWord' -and ($h -and -not $h.Existed)))
        Fact 'all-applied' (YesNo (Test-AllApplied))
        Fact 'unrelated-edge-value-kept' (YesNo ((Get-Val $EDGE 'HomepageLocation').Value -eq 'https://example.test/'))
        # Policies that would sign existing Edge profiles out or stop their sync must never be written.
        $touched = @('BrowserSignin', 'SyncDisabled', 'ForceSync', 'BrowserAddProfileEnabled') | Where-Object { Get-Val $EDGE $_ }
        Fact 'signin-or-sync-of-existing-profiles-touched' (YesNo $touched)
    }
    'already-applied' {
        Seed-Machine
        [void](Invoke-Target)
        $before = Get-Content -LiteralPath $StateFile -Raw
        Reset-Counters
        $exit = Invoke-Target
        Fact 'second-run-writes' $Cnt.Writes
        Fact 'second-run-keycreates' $Cnt.KeyCreates
        Fact 'backup-unchanged' (YesNo ((Get-Content -LiteralPath $StateFile -Raw) -eq $before))
    }
    'undo' {
        Seed-Machine
        $snap0 = Snapshot
        [void](Invoke-Target)
        Fact 'changed-by-apply' (YesNo ((Snapshot) -ne $snap0))
        Reset-Counters
        $exit = Invoke-Target -Undo
        Fact 'registry-restored' (YesNo ((Snapshot) -eq $snap0))
        Fact 'state-after-undo' $(if (Test-Path -LiteralPath $StateFile) { 'exists' } else { 'absent' })
        Fact 'keyremoves' $Cnt.KeyRemoves
        Fact 'second-undo-exit' (Invoke-Target -Undo)
    }
    'undo-whatif' {
        Seed-Machine
        [void](Invoke-Target)
        $snap1 = Snapshot
        Reset-Counters
        $out = Invoke-Target -Undo -WhatIf *>&1 | ForEach-Object { "$_" }
        $exit = $out[-1]
        $out[0..($out.Count - 2)] | ForEach-Object { Write-Host $_ }
        Fact 'keys-previewed-as-removed' @($out | Where-Object { $_ -match '^\s+key .*, would be removed$' }).Count
        Fact 'undo-whatif-writes' $Cnt.Writes
        Fact 'undo-whatif-removes' $Cnt.Removes
        Fact 'undo-whatif-keyremoves' $Cnt.KeyRemoves
        Fact 'registry-unchanged' (YesNo ((Snapshot) -eq $snap1))
        Fact 'state-kept' (YesNo (Test-Path -LiteralPath $StateFile))
    }
    'not-admin' {
        Seed-Machine
        $exit = Invoke-Target
        Fact 'writes' $Cnt.Writes
    }
    'wrong-machine' {
        Seed-Machine
        $exit = Invoke-Target
        Fact 'writes' $Cnt.Writes
    }
    'whatif' {
        Seed-Machine
        $snap0 = Snapshot
        $exit = Invoke-Target -WhatIf
        Fact 'writes' $Cnt.Writes
        Fact 'removes' $Cnt.Removes
        Fact 'keycreates' $Cnt.KeyCreates
        Fact 'state' $(if (Test-Path -LiteralPath $StateFile) { 'exists' } else { 'absent' })
        Fact 'registry-unchanged' (YesNo ((Snapshot) -eq $snap0))
    }
    'never-sets-a-password' {
        Seed-Machine
        [void](Invoke-Target)
        [void](Invoke-Target -Undo)
        $exit = Invoke-Target
        $src = Get-Content -LiteralPath $Target -Raw
        Fact 'nonempty-password-writes' $Cnt.BadPw
        Fact 'forbidden-calls' $Cnt.Forbidden
        Fact 'empty-password-written' (YesNo ($Cnt.EmptyPw -ge 1))
        Fact 'static-scan' $(if ($src -match '(?i)net(\.exe)?\s+user|LocalUser|-Password|SecureString|ADAccountPassword') { 'dirty' } else { 'clean' })
    }
    'existing-default-password' {
        Seed-Machine
        Seed-Value $WL 'DefaultPassword' 'hunter2' 'String'
        $exit = Invoke-Target
        Fact 'writes' $Cnt.Writes
        Fact 'state' $(if (Test-Path -LiteralPath $StateFile) { 'exists' } else { 'absent' })
    }
    'hive-not-loaded' {
        Seed-Machine -HiveLoaded $false
        $exit = Invoke-Target
        $others = (Get-Val $WL 'DefaultUserName').Value -eq 'Mini pc' -and (Get-Val $EDGE 'HideRestoreDialogEnabled').Value -eq 1 -and
                  (Get-Val 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' 'NoAutoRebootWithLoggedOnUsers').Value -eq 1 -and
                  (Get-Val 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' 'SetActiveHours').Value -eq 1 -and
                  (Get-Val $EDGE 'ImplicitSignInEnabled').Value -eq 0
        Fact 'others-applied' (YesNo $others)
        Fact 'toast-written' (YesNo ([bool](Get-Val $PUSH 'ToastEnabled')))
        # Mini pc signs in (the hive loads); running the script again does only the missing setting.
        Seed-Key "Registry::HKEY_USERS\$MiniSid\Software\Microsoft\Windows\CurrentVersion"
        Reset-Counters
        Fact 'later-run-exit' (Invoke-Target)
        Fact 'later-run-writes' $Cnt.Writes
        Fact 'later-run-completed' (YesNo (Test-AllApplied))
    }
}
Remove-Item -LiteralPath $StateFile -ErrorAction SilentlyContinue
exit $exit
