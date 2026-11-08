# Windows settings the TV launcher needs on the mini PC (NUCBOX_M6ULTRA).
#
# Run ON THE MINI PC in an elevated Windows PowerShell (or over ssh tvbox, which
# is an admin session). Always look at the dry run first:
#   powershell -ExecutionPolicy Bypass -File .\minipc-windows-settings.ps1 -WhatIf
#   powershell -ExecutionPolicy Bypass -File .\minipc-windows-settings.ps1
#   powershell -ExecutionPolicy Bypass -File .\minipc-windows-settings.ps1 -Undo
#
# What it sets (each one is printed before it is applied):
#   1. Automatic sign-in as 'Mini pc' with NO password. The account stays
#      password-free; this script never stores or sets a password. If a stored
#      DefaultPassword already exists it stops and leaves it alone.
#   2. Hides the 'tvadmin' account from the sign-in screen (Remote Desktop still
#      works by typing the name).
#   3. Turns off toast notifications for 'Mini pc'. This is a per-user setting in
#      that account's registry hive, which Windows loads only while 'Mini pc' is
#      signed in. If it is not, this one setting FAILS with a clear message and
#      the others still apply; run the script again once 'Mini pc' is signed
#      in and it does only the missing one.
#   4. Windows Update never restarts the PC while someone is signed in (updates
#      then install at the nightly shut down).
#   5. Windows Update active hours 06:00 to 00:00 (18 hours, the most Windows
#      allows), so an update restart never lands in the evening, when the box is on.
#      Microsoft documents that active hours have no effect while setting 4 is
#      enabled; both are set, so whichever Windows honours keeps the evening free.
#   6. Edge: no startup boost, no background mode (closing Edge really closes it),
#      and no "Restore pages?" dialog after a crash.
#   7. Edge: no first-run screens (a remote cannot click through them), and new
#      profiles are not signed in to a Microsoft account automatically. Sign-in
#      and sync of existing profiles are left alone.
#
# -WhatIf  prints every change with its current and new value and writes nothing,
#          not even the backup file.
# -Undo    puts every value back to what it was before the first run: a value that
#          did not exist is removed, and a key this script created is removed only
#          if it is empty.
# Re-running is safe: values that are already right are left alone, and the first
# run's record of the original values is never overwritten.
#
# The record of previous values is $StateFile (JSON, no secrets in it).
# Tested against simulated registries with test-minipc-windows-settings.ps1.

param(
    [switch]$Undo,
    [switch]$WhatIf,
    [string]$StateFile = 'C:\ProgramData\TvLauncher\windows-settings-backup.json'
)

$ErrorActionPreference = 'Stop'
$ExpectedComputer = 'NUCBOX_M6ULTRA'
$FamilyUser = 'Mini pc'

function Test-Administrator {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    Write-Host 'Run this in an elevated (Admin) PowerShell window.'
    exit 1
}
if ($env:COMPUTERNAME -ne $ExpectedComputer) {
    Write-Host ("This script is for the mini PC ({0}); this is {1}. Nothing was changed." -f $ExpectedComputer, $env:COMPUTERNAME)
    exit 1
}

# ---- what to set ----

$Winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$Settings = @(
    @{ Title = "Sign in automatically as '$FamilyUser' with no password (takes effect at the next restart)"
       Key = $Winlogon
       Values = @(
           @{ Name = 'AutoAdminLogon';    Kind = 'String'; Value = '1' }
           @{ Name = 'DefaultUserName';   Kind = 'String'; Value = $FamilyUser }
           @{ Name = 'DefaultDomainName'; Kind = 'String'; Value = $ExpectedComputer }
           @{ Name = 'DefaultPassword';   Kind = 'String'; Value = '' }
       ) }
    @{ Title = 'Hide the tvadmin account from the sign-in screen (Remote Desktop still works by typing the name)'
       Key = "$Winlogon\SpecialAccounts\UserList"
       Values = @( @{ Name = 'tvadmin'; Kind = 'DWord'; Value = 0 } ) }
    @{ Title = "Turn off notifications for '$FamilyUser'"
       UserSubKey = 'Software\Microsoft\Windows\CurrentVersion\PushNotifications'
       Values = @( @{ Name = 'ToastEnabled'; Kind = 'DWord'; Value = 0 } ) }
    @{ Title = 'Windows Update never restarts the PC while someone is signed in (updates install at the nightly shut down)'
       Key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
       Values = @( @{ Name = 'NoAutoRebootWithLoggedOnUsers'; Kind = 'DWord'; Value = 1 } ) }
    @{ Title = 'Windows Update active hours 06:00 to 00:00, so it never restarts the PC in the evening'
       Key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
       Values = @(
           @{ Name = 'SetActiveHours';   Kind = 'DWord'; Value = 1 }
           @{ Name = 'ActiveHoursStart'; Kind = 'DWord'; Value = 6 }
           @{ Name = 'ActiveHoursEnd';   Kind = 'DWord'; Value = 0 }
       ) }
    @{ Title = 'Edge: no startup boost, no background mode, no "Restore pages?" dialog after a crash'
       Key = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
       Values = @(
           @{ Name = 'StartupBoostEnabled';      Kind = 'DWord'; Value = 0 }
           @{ Name = 'BackgroundModeEnabled';    Kind = 'DWord'; Value = 0 }
           @{ Name = 'HideRestoreDialogEnabled'; Kind = 'DWord'; Value = 1 }
       ) }
    @{ Title = 'Edge: no first-run screens, and new profiles are not signed in automatically (existing profiles keep their sign-in and sync)'
       Key = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
       Values = @(
           @{ Name = 'HideFirstRunExperience'; Kind = 'DWord'; Value = 1 }
           @{ Name = 'ImplicitSignInEnabled';  Kind = 'DWord'; Value = 0 }
       ) }
)

# ---- helpers ----

function Show-Key([string]$key) { ($key -replace '^HKLM:\\', 'HKLM\') -replace '^Registry::HKEY_USERS\\', 'HKU\' }
function Show-Val($v) {
    if (-not $v.Exists) { return '(not set)' }
    if ("$($v.Value)" -eq '') { return '(empty)' }
    "$($v.Value)"
}
function Show-Want($want) { if ("$($want.Value)" -eq '') { '(empty)' } else { "$($want.Value)" } }

function Read-Value([string]$key, [string]$name) {
    if (-not (Test-Path -LiteralPath $key)) { return @{ Exists = $false } }
    $p = Get-ItemProperty -LiteralPath $key
    if ($p.PSObject.Properties.Name -notcontains $name) { return @{ Exists = $false } }
    $kind = (Get-Item -LiteralPath $key).GetValueKind($name).ToString()
    if ($kind -notin 'String', 'DWord') {
        throw ("{0} {1} has an unexpected registry type ({2}); not touching it." -f (Show-Key $key), $name, $kind)
    }
    @{ Exists = $true; Value = $p.$name; Kind = $kind }
}
function Test-Same($cur, $kind, $value) {
    $cur.Exists -and $cur.Kind -eq $kind -and "$($cur.Value)" -eq "$value"
}
function Write-Value([string]$key, [string]$name, [string]$kind, $value) {
    if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
    New-ItemProperty -LiteralPath $key -Name $name -PropertyType $kind -Value $value -Force | Out-Null
}
function Get-MissingKeys([string]$key) {
    $missing = @()
    $k = $key
    while (-not (Test-Path -LiteralPath $k)) {
        $missing += $k
        $i = $k.LastIndexOf('\')
        if ($i -lt 0) { break }
        $k = $k.Substring(0, $i)
    }
    $missing
}
function Get-HiveRoot([string]$key) {
    if ($key -match '^(Registry::HKEY_USERS\\[^\\]+)') { $Matches[1] } else { $null }
}
function Test-SameItem($entry, [string]$key, [string]$name) {
    $entry.Path -ieq $key -and $entry.Name -ieq $name
}

# ---- the backup record ----

$Entries = @()       # one per value we changed: Path, Name, Existed, Value, Kind (the previous state)
$CreatedKeys = @()   # keys this script created, in the order found (deepest first within a setting)
if (Test-Path -LiteralPath $StateFile) {
    try {
        $j = Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json
        foreach ($e in @($j.Entries | Where-Object { $_ })) {
            $Entries += [ordered]@{ Path = [string]$e.Path; Name = [string]$e.Name; Existed = [bool]$e.Existed; Value = $e.Value; Kind = [string]$e.Kind }
        }
        $CreatedKeys = @($j.CreatedKeys | Where-Object { $_ } | ForEach-Object { [string]$_ })
    } catch {
        Write-Host ("The backup file {0} cannot be read ({1}). Not touching anything." -f $StateFile, $_.Exception.Message)
        exit 1
    }
}
function Save-State {
    if ($WhatIf) { return }
    if (-not $Entries -and -not $CreatedKeys) {
        if (Test-Path -LiteralPath $StateFile) { Remove-Item -LiteralPath $StateFile -Force }
        return
    }
    $dir = Split-Path -Parent $StateFile
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $doc = [ordered]@{
        Version     = 1
        Computer    = $env:COMPUTERNAME
        Saved       = (Get-Date -Format s)
        Entries     = @($Entries)
        CreatedKeys = @($CreatedKeys)
    }
    ConvertTo-Json -InputObject $doc -Depth 4 | Set-Content -LiteralPath $StateFile -Encoding UTF8
}

# ==== undo ====

if ($Undo) {
    if (-not $Entries -and -not $CreatedKeys) {
        Write-Host ("Nothing to undo: no recorded changes in {0}." -f $StateFile)
        exit 0
    }
    $failures = 0; $restored = 0
    $remaining = @()
    # -WhatIf writes nothing, so it counts what a real undo would remove to report the keys correctly.
    $previewValues = @{}   # lower-case key path -> number of values that would be removed there
    $previewKeys = @()     # created keys that would be removed
    Write-Host ("Undoing the recorded changes ({0})." -f $StateFile)
    for ($i = $Entries.Count - 1; $i -ge 0; $i--) {
        $e = $Entries[$i]
        $label = '{0} {1}' -f (Show-Key $e.Path), $e.Name
        try {
            $hive = Get-HiveRoot $e.Path
            if ($hive -and -not (Test-Path -LiteralPath $hive)) {
                throw ("the registry hive for '{0}' is not loaded (that account is not signed in). Sign in as '{0}' and run -Undo again." -f $FamilyUser)
            }
            $cur = Read-Value $e.Path $e.Name
            if ($e.Existed) {
                if (Test-Same $cur $e.Kind $e.Value) {
                    Write-Host ("  {0}: already back to {1}" -f $label, (Show-Val @{ Exists = $true; Value = $e.Value }))
                } else {
                    Write-Host ("  {0}: currently {1}, will be set back to {2}" -f $label, (Show-Val $cur), (Show-Val @{ Exists = $true; Value = $e.Value }))
                    if (-not $WhatIf) { Write-Value $e.Path $e.Name $e.Kind $e.Value; $restored++ }
                }
            } else {
                if ($cur.Exists) {
                    Write-Host ("  {0}: currently {1}, will be removed (it did not exist before)" -f $label, (Show-Val $cur))
                    if ($WhatIf) { $previewValues[$e.Path.ToLower()] = 1 + [int]$previewValues[$e.Path.ToLower()] }
                    else { Remove-ItemProperty -LiteralPath $e.Path -Name $e.Name; $restored++ }
                } else {
                    Write-Host ("  {0}: already absent" -f $label)
                }
            }
        } catch {
            $failures++
            $remaining = @($e) + $remaining
            Write-Host ("  {0}: FAILED, {1}" -f $label, $_.Exception.Message)
        }
    }
    $keptKeys = @()
    foreach ($k in @($CreatedKeys | Sort-Object { $_.Length } -Descending)) {
        try {
            $hive = Get-HiveRoot $k
            if ($hive -and -not (Test-Path -LiteralPath $hive)) {
                throw ("the registry hive for '{0}' is not loaded. Sign in as '{0}' and run -Undo again." -f $FamilyUser)
            }
            if (-not (Test-Path -LiteralPath $k)) {
                Write-Host ("  key {0}: already gone" -f (Show-Key $k))
                continue
            }
            $item = Get-Item -LiteralPath $k
            $subKeys = $item.SubKeyCount
            $values = $item.ValueCount
            if ($WhatIf) {
                $values -= [int]$previewValues[$k.ToLower()]
                $subKeys -= @($previewKeys | Where-Object { $_.Substring(0, $_.LastIndexOf('\')) -ieq $k }).Count
            }
            if ($subKeys -eq 0 -and $values -eq 0) {
                if ($WhatIf) {
                    Write-Host ("  key {0}: created by this script and empty after the undo, would be removed" -f (Show-Key $k))
                    $previewKeys += $k
                } else {
                    Write-Host ("  key {0}: created by this script and empty, will be removed" -f (Show-Key $k))
                    Remove-Item -LiteralPath $k
                }
            } else {
                Write-Host ("  key {0}: left in place, it still has contents" -f (Show-Key $k))
                if ($failures) { $keptKeys += $k }
            }
        } catch {
            $failures++
            $keptKeys += $k
            Write-Host ("  key {0}: FAILED, {1}" -f (Show-Key $k), $_.Exception.Message)
        }
    }
    if ($WhatIf) {
        Write-Host 'Nothing was written. This was a dry run (-WhatIf); the backup file is unchanged.'
        exit [int]($failures -gt 0)
    }
    $Entries = $remaining
    $CreatedKeys = $keptKeys
    Save-State
    if ($failures) {
        Write-Host ("{0} item(s) FAILED and stay recorded in {1}; fix the cause and run -Undo again." -f $failures, $StateFile)
        exit 1
    }
    Write-Host ("Undone: {0} value(s) restored. The backup file has been removed." -f $restored)
    exit 0
}

# ==== apply ====

# The family account is password-free and must stay so: refuse to run over a stored password.
$stored = Read-Value $Winlogon 'DefaultPassword'
if ($stored.Exists -and "$($stored.Value)" -ne '') {
    Write-Host 'A DefaultPassword is already stored in the Winlogon registry key (its value is not shown here).'
    Write-Host 'This script keeps the account password-free and is not touching it. Decide what to do with it, then run this again.'
    exit 1
}

$sid = $null
$sidLooked = $false
$total = $Settings.Count
$failures = 0
$changes = 0
$n = 0
foreach ($s in $Settings) {
    $n++
    Write-Host ''
    Write-Host ("Setting {0} of {1}: {2}" -f $n, $total, $s.Title)
    try {
        $key = $s.Key
        if ($s.UserSubKey) {
            if (-not $sidLooked) {
                $acct = Get-CimInstance -ClassName Win32_UserAccount -Filter "Name='$FamilyUser' AND LocalAccount=True"
                if ($acct) { $sid = @($acct)[0].SID }
                $sidLooked = $true
            }
            if (-not $sid) { throw ("there is no local account named '{0}' on this PC." -f $FamilyUser) }
            $hive = "Registry::HKEY_USERS\$sid"
            if (-not (Test-Path -LiteralPath $hive)) {
                throw ("the settings hive for '{0}' is not loaded, because that account is not signed in. Sign in as '{0}' and run this script again; it will apply only this setting." -f $FamilyUser)
            }
            $key = "$hive\$($s.UserSubKey)"
        }

        $plan = @()
        foreach ($v in $s.Values) {
            $cur = Read-Value $key $v.Name
            $plan += @{ Value = $v; Current = $cur; Same = (Test-Same $cur $v.Kind $v.Value) }
        }
        $missing = @(Get-MissingKeys $key)
        foreach ($p in $plan) {
            $label = '{0} {1}' -f (Show-Key $key), $p.Value.Name
            if ($p.Same) {
                Write-Host ("  {0}: already {1}, no change" -f $label, (Show-Want $p.Value))
            } else {
                Write-Host ("  {0}: currently {1}, will be set to {2}" -f $label, (Show-Val $p.Current), (Show-Want $p.Value))
            }
        }
        $todo = @($plan | Where-Object { -not $_.Same })
        if ($todo.Count -and $missing.Count) {
            foreach ($m in $missing) { Write-Host ("  key {0} does not exist and will be created" -f (Show-Key $m)) }
        }
        if (-not $todo.Count -or $WhatIf) { $changes += $todo.Count; continue }

        # Record the previous state first, so a failed write can still be undone.
        foreach ($p in $todo) {
            if (-not ($Entries | Where-Object { Test-SameItem $_ $key $p.Value.Name })) {
                $Entries += [ordered]@{
                    Path    = $key
                    Name    = $p.Value.Name
                    Existed = [bool]$p.Current.Exists
                    Value   = $(if ($p.Current.Exists) { $p.Current.Value } else { $null })
                    Kind    = $(if ($p.Current.Exists) { $p.Current.Kind } else { $p.Value.Kind })
                }
            }
        }
        foreach ($m in $missing) { if ($CreatedKeys -inotcontains $m) { $CreatedKeys += $m } }
        Save-State
        foreach ($p in $todo) {
            Write-Value $key $p.Value.Name $p.Value.Kind $p.Value.Value
            $changes++
        }
    } catch {
        $failures++
        Write-Host ("  FAILED: {0}" -f $_.Exception.Message)
    }
}

Write-Host ''
if ($WhatIf) {
    Write-Host ("Nothing was written. This was a dry run (-WhatIf): {0} value(s) would change, and no backup file was created." -f $changes)
} elseif ($changes) {
    Write-Host ("Applied {0} change(s). The previous values are recorded in {1}; undo with -Undo." -f $changes, $StateFile)
} elseif (-not $failures) {
    Write-Host ("Nothing to change: all {0} settings are already applied." -f $total)
}
if ($failures) {
    Write-Host ("{0} of {1} settings FAILED, see above. The others were handled." -f $failures, $total)
    exit 1
}
exit 0
