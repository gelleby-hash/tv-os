# Checks that the apps are the same in the three launcher configs: the repository's
# launcher\src\TvLauncher\launcher.json, the dev PC's %LOCALAPPDATA%\TvLauncher\dev-launcher.json and the
# mini PC's C:\ProgramData\TvLauncher\launcher.json (read over `ssh tvbox`). Run on the DEV PC:
#   powershell -ExecutionPolicy Bypass -File .\setup\check-app-lists.ps1
#
# Every app entry must match in full: the same ids in the same order, every field, and each version with
# all its fields. The other settings (Location, AllowClose, PhoneRemote, keys...) belong to each machine
# and are not compared. deploy-launcher.ps1 keeps the mini PC's launcher.json, so an app or a field added
# to the repository has to be added to the other two by hand; this check is what catches a missed one
# (owner, 2026-11-02: Channel 4 was added to the mini PC but never to the dev PC).
#
# A mini PC with no launcher.json yet passes (the first deploy creates it from the repository's).
# -MiniPcConfig reads a local file instead of the mini PC (the tests use it).
#
# Exit codes: 0 the apps match, 1 they differ or a config could not be read. Run by deploy-launcher.ps1
# before it builds; tested by test-check-app-lists.ps1.

param(
    [string]$RepoConfig = (Join-Path $PSScriptRoot '..\launcher\src\TvLauncher\launcher.json'),
    [string]$DevConfig = (Join-Path $env:LOCALAPPDATA 'TvLauncher\dev-launcher.json'),
    [string]$MiniPcConfig
)

$ErrorActionPreference = 'Stop'

# A value as text with its keys sorted, so two entries compare equal whatever their layout and key order.
function Canon($v) {
    if ($null -eq $v) { return 'null' }
    if ($v -is [string]) { return (ConvertTo-Json $v -Compress) }
    if ($v -is [bool]) { return $v.ToString().ToLowerInvariant() }
    if ($v -is [array]) { return '[' + ((@($v) | ForEach-Object { Canon $_ }) -join ',') + ']' }
    if ($v -is [System.Management.Automation.PSCustomObject]) {
        return '{' + ((@($v.PSObject.Properties) | Sort-Object Name | ForEach-Object { (ConvertTo-Json $_.Name -Compress) + ':' + (Canon $_.Value) }) -join ',') + '}'
    }
    return [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $v)
}

function Short([string]$s) { if ($s.Length -gt 70) { $s.Substring(0, 67) + '...' } else { $s } }

# The Apps list of a config's text ($null text: no file). A config without Apps has no apps.
function Read-Apps([string]$Label, [string]$Text) {
    try { $cfg = $Text.TrimStart([char]0xFEFF) | ConvertFrom-Json } catch { throw "could not read the $Label's config: $($_.Exception.Message)" }
    if ($null -eq $cfg) { throw "could not read the $Label's config: it is empty" }
    if ($cfg.PSObject.Properties.Name -notcontains 'Apps' -or $null -eq $cfg.Apps) { return @() }
    @($cfg.Apps)
}

function Read-Local([string]$Label, [string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "the $Label's config was not found: $Path" }
    [IO.File]::ReadAllText($Path)
}

# The mini PC's launcher.json as text, or $null when it has none yet.
function Read-MiniPc {
    $remote = @'
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'NUCBOX_M6ULTRA'){throw "Wrong machine: $env:COMPUTERNAME"}
$C='C:\ProgramData\TvLauncher\launcher.json'
if(Test-Path $C){'CONFIG64='+[Convert]::ToBase64String([IO.File]::ReadAllBytes($C))}else{'CONFIG64=absent'}
'@
    $text = "try{`n$remote`n}catch{Write-Output ('REMOTE-ERROR: '+`$_.Exception.Message);throw}"
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($text))
    $lines = @(& ssh -o BatchMode=yes tvbox powershell -NoProfile -NonInteractive -EncodedCommand $b64)
    $code = $LASTEXITCODE
    $err = @($lines | Where-Object { $_ -like 'REMOTE-ERROR: *' })
    if ($err) { throw "could not read the mini PC's config: $($err[0].Substring(14))" }
    if ($code -ne 0) { throw "could not read the mini PC's config (ssh exit $code)" }
    $line = @($lines | Where-Object { $_ -like 'CONFIG64=*' } | Select-Object -Last 1)
    if (-not $line) { throw "could not read the mini PC's config: it sent no CONFIG64 line" }
    $value = $line[0].Substring(9).Trim()
    if ($value -eq 'absent') { return $null }
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($value))
}

function Describe($apps) {
    if (-not $apps.Count) { return '(no apps)' }
    ($apps | ForEach-Object {
        $v = @($_.Versions | Where-Object { $_ })
        if ($v.Count) { '{0} ({1})' -f $_.Id, (($v | ForEach-Object { $_.Id }) -join ', ') } else { $_.Id }
    }) -join ', '
}

# The differences between two entries' fields; $Name is "app" or "app/version".
function Compare-Fields([string]$Name, $Want, $Have, [string]$Label, [string[]]$Skip) {
    $keys = @(@($Want.PSObject.Properties.Name) + @($Have.PSObject.Properties.Name) | Where-Object { $Skip -notcontains $_ } | Sort-Object -Unique)
    foreach ($k in $keys) {
        $w = if ($Want.PSObject.Properties.Name -contains $k) { Canon $Want.$k } else { '(none)' }
        $h = if ($Have.PSObject.Properties.Name -contains $k) { Canon $Have.$k } else { '(none)' }
        if ($w -ne $h) { "{0} {1}: repository {2}, {3} {4}" -f $Name, $k, (Short $w), $Label, (Short $h) }
    }
}

# Everything in which one config's apps differ from the repository's.
function Compare-Apps($Want, $Have, [string]$Label) {
    $wantIds = @($Want | ForEach-Object { $_.Id }); $haveIds = @($Have | ForEach-Object { $_.Id })
    foreach ($id in $wantIds) { if ($haveIds -notcontains $id) { "$id is missing on the $Label" } }
    foreach ($id in $haveIds) { if ($wantIds -notcontains $id) { "$id is on the $Label but not in the repository" } }
    $common = @($wantIds | Where-Object { $haveIds -contains $_ })
    if (($common -join ',') -ne (@($haveIds | Where-Object { $wantIds -contains $_ }) -join ',')) {
        "the $Label lists the apps in another order: $($haveIds -join ', ')"
    }
    foreach ($id in $common) {
        $w = @($Want | Where-Object { $_.Id -eq $id })[0]; $h = @($Have | Where-Object { $_.Id -eq $id })[0]
        Compare-Fields $id $w $h $Label @('Versions')
        $wv = @($w.Versions | Where-Object { $_ }); $hv = @($h.Versions | Where-Object { $_ })
        $wvIds = @($wv | ForEach-Object { $_.Id }); $hvIds = @($hv | ForEach-Object { $_.Id })
        foreach ($v in $wvIds) { if ($hvIds -notcontains $v) { "$id/$v is missing on the $Label" } }
        foreach ($v in $hvIds) { if ($wvIds -notcontains $v) { "$id/$v is on the $Label but not in the repository" } }
        if ((@($wvIds | Where-Object { $hvIds -contains $_ }) -join ',') -ne (@($hvIds | Where-Object { $wvIds -contains $_ }) -join ',')) {
            "the $Label lists $id's versions in another order: $($hvIds -join ', ')"
        }
        foreach ($v in @($wvIds | Where-Object { $hvIds -contains $_ })) {
            Compare-Fields "$id/$v" (@($wv | Where-Object { $_.Id -eq $v })[0]) (@($hv | Where-Object { $_.Id -eq $v })[0]) $Label @()
        }
    }
}

try {
    $repo = Read-Apps 'repository' (Read-Local 'repository' $RepoConfig)
    $dev = Read-Apps 'dev PC' (Read-Local 'dev PC' $DevConfig)
    if ($MiniPcConfig) {
        $miniText = if (Test-Path -LiteralPath $MiniPcConfig) { [IO.File]::ReadAllText($MiniPcConfig) } else { $null }
        $miniFrom = $MiniPcConfig
    } else {
        $miniText = Read-MiniPc
        $miniFrom = 'tvbox C:\ProgramData\TvLauncher\launcher.json'
    }

    Write-Host ("repository  {0}" -f (Describe $repo))
    Write-Host ("dev PC      {0}" -f (Describe $dev))
    $problems = @(Compare-Apps $repo $dev 'dev PC')
    if ($null -eq $miniText) {
        Write-Host 'mini PC     no launcher.json yet (the deploy creates it from the repository''s)'
    } else {
        $mini = Read-Apps 'mini PC' $miniText
        Write-Host ("mini PC     {0}" -f (Describe $mini))
        $problems += @(Compare-Apps $repo $mini 'mini PC')
    }
    Write-Host ("(repository {0}; dev PC {1}; mini PC {2})" -f $RepoConfig, $DevConfig, $miniFrom)

    if ($problems.Count) {
        foreach ($p in $problems) { Write-Host "DIFFER: $p" }
        Write-Host ("{0} difference(s). Make the dev PC's and the mini PC's app entries match the repository's (back each file up first), then run this again." -f $problems.Count)
        exit 1
    }
    Write-Host 'The app lists match.'
    exit 0
} catch {
    Write-Host "FAILED: $($_.Exception.Message)"
    exit 1
}
