# Tests deploy-launcher.ps1's control flow on any PC, without contacting the mini PC:
# `dotnet`, `ssh` and `scp` are replaced by fakes. The fake ssh decodes the
# -EncodedCommand text the script sends and RUNS it here, against a temp folder that
# stands in for C:\Program Files\TvLauncher and C:\ProgramData\TvLauncher, with fake
# process and scheduled-task cmdlets, so the file swaps, the config rule and the task
# settings are checked for real rather than by reading the text. A running launcher
# locks its install folder in the simulation, as it does on the mini PC. Aliases
# outrank functions and cmdlets in PowerShell, so the fakes win.
#
#   powershell -ExecutionPolicy Bypass -File .\test-deploy-launcher.ps1
#
# Exits non-zero if any scenario does not behave as expected.

param([string]$Scenario)

$Target = Join-Path $PSScriptRoot 'deploy-launcher.ps1'

$Scenarios = [ordered]@{
    # name = expected exit code, text that must appear, text that must not appear
    'first-deploy' = @{ Exit = 0
        Must = @('DONE', '[harness] call-order=ssh dotnet ssh scp scp ssh ssh ssh', '[harness] install-version=v1', '[harness] previous=absent',
                 '[harness] config=created-from-repo', '[harness] staging-gone=yes', '[harness] bad-ssh-invocations=0', '[harness] encoded-length-ok=yes',
                 '[harness] task-principal=NUCBOX_M6ULTRA\Mini pc', '[harness] task-logon-type=Interactive', '[harness] task-trigger-user=NUCBOX_M6ULTRA\Mini pc',
                 '[harness] task-restart=3x1min', '[harness] task-time-limit=0', '[harness] task-multiple-instances=Parallel',
                 '[harness] task-watchdog=PT1M/indefinitely',
                 '[harness] task-on-battery=yes', '[harness] task-action-exe=install-folder-exe', '[harness] task-enabled=yes', '[harness] task-started=1',
                 '[harness] password-or-elevation-in-script=no', '[harness] kills-by-name=0', '[harness] launcher-running-at-end=yes',
                 '[harness] acl-reset-on-install-folder=v1')
        MustNot = @('FAILED') }
    'redeploy-keeps-previous-and-config' = @{ Exit = 0
        Must = @('DONE', '[harness] install-version=v2', '[harness] previous-version=v1', '[harness] config-kept-owner-edit=yes',
                 '[harness] swap-while-running=0', '[harness] launcher-and-its-webview-stopped=yes', '[harness] other-processes-alive=yes',
                 '[harness] task-started=2', '[harness] launcher-running-at-end=yes', '[harness] staging-gone=yes', '[harness] task-enabled=yes')
        MustNot = @('FAILED') }
    'rollback' = @{ Exit = 0
        Must = @('Rolled back', '[harness] install-version=v1', '[harness] previous-version=v2', '[harness] dotnet-calls=2',
                 '[harness] config-kept-owner-edit=yes', '[harness] swap-while-running=0', '[harness] launcher-and-its-webview-stopped=yes',
                 '[harness] other-processes-alive=yes', '[harness] task-started=3', '[harness] launcher-running-at-end=yes',
                 '[harness] second-rollback-exit=0', '[harness] after-second-rollback=v2/v1', '[harness] acl-reset-on-install-folder=v1')
        MustNot = @('FAILED') }
    'rollback-without-previous' = @{ Exit = 1
        Must = @('No previous version', 'FAILED', '[harness] install-version=v1', '[harness] previous=absent', '[harness] rollback-stops=0',
                 '[harness] launcher-running-at-end=yes', '[harness] task-started=1')
        MustNot = @('Rolled back') }
    'publish-fails-stops-before-copy' = @{ Exit = 1
        Must = @('publish failed', 'FAILED', '[harness] ssh-calls=1', '[harness] scp-calls=0', '[harness] install-folder=absent')
        MustNot = @('DONE') }
    # The apps must match in the repository, the dev PC and the mini PC before anything is built (owner, 2026-11-02).
    'apps-differ-on-dev-stops-before-build' = @{ Exit = 1
        Must = @('DIFFER: plex is on the dev PC but not in the repository', 'nothing was built or sent', 'FAILED', '[harness] dotnet-calls=0',
                 '[harness] scp-calls=0', '[harness] install-folder=absent')
        MustNot = @('DONE') }
    'apps-differ-on-mini-pc-stops-before-build' = @{ Exit = 1
        Must = @('DIFFER: plex is on the mini PC but not in the repository', 'nothing was built or sent', 'FAILED', '[harness] dotnet-calls=1',
                 '[harness] install-version=v1', '[harness] stops-during-failed-run=0', '[harness] launcher-running-at-end=yes')
        MustNot = @('DONE') }
    'scp-fails-leaves-install-alone' = @{ Exit = 1
        Must = @('FAILED', '[harness] install-version=v1', '[harness] previous=absent', '[harness] stops-during-failed-run=0',
                 '[harness] launcher-running-at-end=yes', '[harness] ssh-after-scp-failure=0')
        MustNot = @('DONE') }
    'wrong-machine' = @{ Exit = 1
        Must = @('Wrong machine', 'FAILED', '[harness] scp-calls=0', '[harness] install-folder=absent', '[harness] stops=0')
        MustNot = @('DONE') }
    'swap-fails-restarts-old-version' = @{ Exit = 1
        Must = @('FAILED', 'start the version that was in place', '[harness] install-version=v1', '[harness] launcher-running-at-end=yes',
                 '[harness] config-kept-owner-edit=yes')
        MustNot = @('DONE') }
    'register-fails-reenables-and-starts' = @{ Exit = 1
        Must = @('FAILED', 'REMOTE-ERROR', 'task XML is malformed', 'start the version that was in place', '[harness] install-version=v2',
                 '[harness] task-enabled=yes', '[harness] launcher-running-at-end=yes', '[harness] task-started=2', '[harness] other-processes-alive=yes')
        MustNot = @('DONE') }
    'rollback-start-fails-reenables-and-starts' = @{ Exit = 1
        Must = @('FAILED', 'Enable-ScheduledTask: access denied', 'start the version that was in place', '[harness] install-version=v1',
                 '[harness] previous-version=v2', '[harness] task-enabled=yes', '[harness] launcher-running-at-end=yes', '[harness] task-started=3',
                 '[harness] other-processes-alive=yes')
        MustNot = @('Rolled back') }
    'second-move-fails-restores-old-install' = @{ Exit = 1
        Must = @('FAILED', 'second move', '[harness] install-version=v1', '[harness] previous=absent', '[harness] launcher-running-at-end=yes',
                 '[harness] task-enabled=yes', '[harness] config-kept-owner-edit=yes', '[harness] other-processes-alive=yes')
        MustNot = @('DONE') }
    'rollback-second-move-fails-restores-install' = @{ Exit = 1
        Must = @('FAILED', 'second move', '[harness] install-version=v2', '[harness] previous-version=v1', '[harness] launcher-running-at-end=yes',
                 '[harness] task-enabled=yes', '[harness] other-processes-alive=yes')
        MustNot = @('Rolled back') }
    'deployed-but-not-running' = @{ Exit = 2
        Must = @('NOT running', 'next sign-in', 'No logged-on interactive user session', '[harness] install-version=v1', '[harness] task-enabled=yes')
        MustNot = @('DONE', 'FAILED', 'probably not signed in') }
    # The install folder came from ProgramData staging with its ACL; if the reset to Program Files'
    # inherited ACL fails, the deploy fails and the launcher is started again.
    'acl-reset-fails-restarts' = @{ Exit = 1
        Must = @('FAILED', 'icacls', 'start the version that was in place', '[harness] install-version=v2',
                 '[harness] launcher-running-at-end=yes', '[harness] task-enabled=yes')
        MustNot = @('DONE') }
    # Edge (Disney+) was started by the launcher inside the task's job object, so the task still
    # counts as Running after the launcher itself is stopped. With IgnoreNew the start would do nothing.
    'redeploy-while-job-keeps-task-running' = @{ Exit = 0
        Must = @('DONE', '[harness] install-version=v2', '[harness] task-started=2', '[harness] launcher-running-at-end=yes',
                 '[harness] other-processes-alive=yes', '[harness] ignored-starts=0')
        MustNot = @('FAILED', 'NOT running') }
    # The launcher crashed while Edge kept the task Running; the 1-minute repetition must bring it back,
    # and must not start a second launcher while one is up.
    'crash-watchdog-restarts' = @{ Exit = 0
        Must = @('[harness] watchdog-with-launcher-up-starts-another=no', '[harness] after-crash-running=no',
                 '[harness] after-watchdog-running=yes', '[harness] other-processes-alive=yes')
        MustNot = @('FAILED') }
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

# ---- one scenario: a simulated dev PC folder and mini PC, then run the real script ----

$Work = Join-Path $env:TEMP ("tvos-deploy-{0}-{1}" -f $Scenario, $PID)
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Path $Work | Out-Null
$Pub = Join-Path $Work 'dev\artifacts\publish'
$Cfg = Join-Path $Work 'dev\launcher.json'
New-Item -ItemType Directory -Path (Split-Path $Cfg) | Out-Null
Set-Content -LiteralPath $Cfg -Value '{ "Apps": [], "Location": { "Latitude": 10, "Longitude": 20 } }' -Encoding ascii
$DevCfg = Join-Path $Work 'dev\dev-launcher.json'
Set-Content -LiteralPath $DevCfg -Value '{ "Apps": [], "AllowClose": true }' -Encoding ascii

$env:COMPUTERNAME = if ($Scenario -eq 'wrong-machine') { 'SOME-OTHER-PC' } else { 'NUCBOX_M6ULTRA' }

$global:Sim = @{
    PF = Join-Path $Work 'minipc\ProgramFiles\TvLauncher'
    PD = Join-Path $Work 'minipc\ProgramData\TvLauncher'
    Procs = New-Object 'System.Collections.Generic.List[hashtable]'
    NextPid = 200
    Calls = New-Object 'System.Collections.Generic.List[string]'
    SshTexts = New-Object 'System.Collections.Generic.List[string]'
    Stops = New-Object 'System.Collections.Generic.List[string]'
    Task = $null
    Starts = 0
    SwapWhileRunning = 0
    BadSsh = 0
    MaxB64 = 0
    SshAfterScpFailure = 0
    Publishes = 0
    Version = 'v1'
    PublishFails = $false
    ScpFails = $false
    ScpFailed = $false
    StartThrows = $false
    SwapFails = $false
    SecondMoveFails = $false
    RegisterFails = $false
    EnableFailsOnce = $false
    IgnoredStarts = 0
    AclResets = New-Object 'System.Collections.Generic.List[string]'
    IcaclsFails = $false
}
New-Item -ItemType Directory -Path (Split-Path $Sim.PF) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $Sim.PD) -Force | Out-Null

function Fact([string]$name, $value) { Write-Host ("[harness] {0}={1}" -f $name, $value) }
function YesNo($b) { if ($b) { 'yes' } else { 'no' } }
function Map-Paths([string]$s) { $s.Replace('C:\ProgramData\TvLauncher', $Sim.PD).Replace('C:\Program Files\TvLauncher', $Sim.PF) }
function Get-Procs([string]$name) { @($Sim.Procs | Where-Object { $_.Name -eq $name }) }
# InJob: started by the scheduled task (the launcher, and anything it started), so Task Scheduler
# counts the task as Running while any of them is alive.
function Add-Proc([string]$name, [string]$cmd, [switch]$InJob) { $Sim.NextPid++; $Sim.Procs.Add(@{ Id = $Sim.NextPid; Name = $name; CommandLine = $cmd; InJob = [bool]$InJob }); $Sim.NextPid }
function Test-TaskRunning { @($Sim.Procs | Where-Object { $_.InJob }).Count -gt 0 }
function Reset-Run { $Sim.Calls.Clear(); $Sim.SshTexts.Clear(); $Sim.Stops.Clear(); $Sim.SshAfterScpFailure = 0 }

# Processes that are on the mini PC whatever we do and must survive a deploy.
$Sim.Procs.Add(@{ Id = 102; Name = 'msedgewebview2'; CommandLine = '"msedgewebview2.exe" --user-data-dir="C:\Users\Mini pc\AppData\Local\Microsoft\Teams\EBWebView"' })
$Sim.Procs.Add(@{ Id = 103; Name = 'msedge'; CommandLine = '"msedge.exe" --app=https://www.disneyplus.com --user-data-dir="C:\Users\Mini pc\AppData\Local\TvLauncher\edge-disney"' })
$Sim.Procs.Add(@{ Id = 104; Name = 'VacuumTube'; CommandLine = '"VacuumTube.exe" --remote-debugging-port=9223' })

function Fake-Dotnet {
    $Sim.Calls.Add('dotnet'); $Sim.Publishes++
    if ($Sim.PublishFails) { Write-Host 'error NETSDK1004: fake publish failure'; $global:LASTEXITCODE = 1; return }
    $out = $args[[array]::IndexOf($args, '-o') + 1]
    New-Item -ItemType Directory -Path (Join-Path $out 'web\js') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $out 'TvLauncher.exe') -Value ("exe {0}" -f $Sim.Version) -Encoding ascii
    Set-Content -LiteralPath (Join-Path $out 'TvLauncher.dll') -Value ("dll {0}" -f $Sim.Version) -Encoding ascii
    Set-Content -LiteralPath (Join-Path $out 'launcher.json') -Value 'bundled copy' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $out 'web\index.html') -Value '<html></html>' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $out 'web\js\app.js') -Value ("js {0}" -f $Sim.Version) -Encoding ascii
    $global:LASTEXITCODE = 0
}

function Fake-Ssh {
    $Sim.Calls.Add('ssh')
    if ($Sim.ScpFailed) { $Sim.SshAfterScpFailure++ }
    $i = [array]::IndexOf($args, 'tvbox')
    $shape = ($i -ge 0) -and ($args[$i + 1] -eq 'powershell') -and ($args[$i + 2] -eq '-NoProfile') -and
             ($args[$i + 3] -eq '-NonInteractive') -and ($args[$i + 4] -eq '-EncodedCommand') -and ($args.Count -eq $i + 6)
    if (-not $shape) { $Sim.BadSsh++; $global:LASTEXITCODE = 255; return }
    $b64 = $args[-1]
    $Sim.MaxB64 = [Math]::Max($Sim.MaxB64, $b64.Length)
    $text = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($b64))
    $Sim.SshTexts.Add($text)
    $global:LASTEXITCODE = 0
    try { & ([scriptblock]::Create((Map-Paths $text))) } catch { $global:LASTEXITCODE = 1 }
}

function Fake-Scp {
    $Sim.Calls.Add('scp')
    if ($Sim.ScpFails) { Write-Host 'scp: Connection reset by peer'; $global:LASTEXITCODE = 1; $Sim.ScpFailed = $true; return }
    $recursive = $args -contains '-r'
    $rest = @($args | Where-Object { $_ -notlike '-*' -and $_ -ne 'BatchMode=yes' })
    $src = Join-Path (Get-Location).Path $rest[0]
    if ($rest[1] -notlike 'tvbox:*') { Write-Host "scp: bad destination $($rest[1])"; $global:LASTEXITCODE = 1; return }
    $dst = Map-Paths ($rest[1].Substring(6).Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $dst -PathType Container)) { Write-Host "scp: $($rest[1]): No such file or directory"; $global:LASTEXITCODE = 1; return }
    if ((Test-Path -LiteralPath $src -PathType Container) -and -not $recursive) { Write-Host 'scp: not a regular file'; $global:LASTEXITCODE = 1; return }
    Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
    $global:LASTEXITCODE = 0
}

function Fake-GetProcess {
    [CmdletBinding()] param([Parameter(Position = 0)][string]$Name)
    Get-Procs $Name | ForEach-Object { [pscustomobject]@{ Id = $_.Id; Name = $_.Name } }
}
function Fake-StopProcess {
    [CmdletBinding()] param([Parameter(ValueFromPipelineByPropertyName)][int[]]$Id, [switch]$Force)
    process {
        foreach ($one in $Id) {
            $p = $Sim.Procs | Where-Object { $_.Id -eq $one } | Select-Object -First 1
            if ($p) { $Sim.Stops.Add(("{0}:{1}" -f $p.Name, $p.Id)); [void]$Sim.Procs.Remove($p) }
        }
    }
}
function Fake-GetCim {
    [CmdletBinding()] param([string]$ClassName, [string]$Filter)
    $n = if ($Filter -match "Name='([^']+)'") { $Matches[1] -replace '\.exe$', '' } else { $null }
    $Sim.Procs | Where-Object { -not $n -or $_.Name -eq $n } | ForEach-Object {
        [pscustomobject]@{ ProcessId = $_.Id; Name = "$($_.Name).exe"; CommandLine = $_.CommandLine }
    }
}
function Fake-MoveItem {
    [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [Parameter(Position = 1)][string]$Destination, [switch]$Force)
    if ($Sim.SecondMoveFails -and $Destination -eq $Sim.PF) { $Sim.SecondMoveFails = $false; throw "Access to the path '$Destination' is denied (second move)." }
    if ($Path -eq $Sim.PF -and (Get-Procs 'TvLauncher').Count) { $Sim.SwapWhileRunning++; throw "Access to the path '$Path' is denied (files in use)." }
    if ($Path -eq $Sim.PF -and $Sim.SwapFails) { throw "Access to the path '$Path' is denied (antivirus holds it)." }
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $Path -Destination $Destination
}
function Fake-NewAction { [CmdletBinding()] param([string]$Execute, [string]$WorkingDirectory, [string]$Argument) [pscustomobject]@{ Execute = $Execute; WorkingDirectory = $WorkingDirectory } }
function Fake-NewTrigger {
    [CmdletBinding()] param([switch]$AtLogOn, [string]$User, [switch]$Once, $At, [TimeSpan]$RepetitionInterval, [TimeSpan]$RepetitionDuration)
    # Like the real cmdlet: an interval without a duration repeats indefinitely (Duration empty).
    $rep = if ($RepetitionInterval) { [pscustomobject]@{ Interval = [Xml.XmlConvert]::ToString($RepetitionInterval)
        Duration = $(if ($RepetitionDuration) { [Xml.XmlConvert]::ToString($RepetitionDuration) } else { '' }) } } else { $null }
    [pscustomobject]@{ AtLogOn = [bool]$AtLogOn; UserId = $User; Repetition = $rep }
}
function Fake-NewPrincipal { [CmdletBinding()] param([string]$UserId, [string]$LogonType, [string]$RunLevel) [pscustomobject]@{ UserId = $UserId; LogonType = $LogonType; RunLevel = $RunLevel } }
function Fake-NewSettings {
    [CmdletBinding()] param([switch]$AllowStartIfOnBatteries, [switch]$DontStopIfGoingOnBatteries, [TimeSpan]$ExecutionTimeLimit,
                            [int]$RestartCount, [TimeSpan]$RestartInterval, [string]$MultipleInstances)
    [pscustomobject]@{ AllowStartIfOnBatteries = [bool]$AllowStartIfOnBatteries; DontStopIfGoingOnBatteries = [bool]$DontStopIfGoingOnBatteries
        ExecutionTimeLimit = $ExecutionTimeLimit; RestartCount = $RestartCount; RestartInterval = $RestartInterval; MultipleInstances = $MultipleInstances }
}
function Fake-RegisterTask {
    [CmdletBinding()] param([string]$TaskName, $Action, $Trigger, $Principal, $Settings, [switch]$Force)
    if ($Sim.RegisterFails) { throw 'Register-ScheduledTask: the task XML is malformed' }
    $Sim.Task = [pscustomobject]@{ TaskName = $TaskName; Actions = @($Action); Triggers = @($Trigger); Principal = $Principal; Settings = $Settings; Enabled = $true }
    $Sim.Task
}
function Fake-GetTask { [CmdletBinding()] param([string]$TaskName) if ($Sim.Task -and $Sim.Task.TaskName -eq $TaskName) { $Sim.Task } }
function Fake-DisableTask { [CmdletBinding()] param([string]$TaskName) if ($Sim.Task) { $Sim.Task.Enabled = $false } else { Write-Error 'No task' } }
function Fake-EnableTask {
    [CmdletBinding()] param([string]$TaskName)
    if ($Sim.EnableFailsOnce) { $Sim.EnableFailsOnce = $false; throw 'Enable-ScheduledTask: access denied' }
    if ($Sim.Task) { $Sim.Task.Enabled = $true } else { Write-Error 'No task' }
}
function Fake-StopTask {
    # Models Task Scheduler's job object: stopping the task ends its whole process tree, and
    # Edge and VacuumTube are children of the launcher. The script must never call this.
    [CmdletBinding()] param([string]$TaskName)
    foreach ($p in @($Sim.Procs | Where-Object { $_.Name -in 'TvLauncher', 'msedge', 'VacuumTube' -or $_.CommandLine -like '*\TvLauncher\WebView2*' })) {
        $Sim.Stops.Add(("task:{0}:{1}" -f $p.Name, $p.Id)); [void]$Sim.Procs.Remove($p)
    }
}
function Fake-StartTask {
    # Task Scheduler: a task with a live process in its job is Running, and with IgnoreNew a start
    # (by hand or by a trigger) then does nothing. With Parallel a new instance starts, and the
    # launcher's single-instance mutex makes it exit at once if a launcher is already running.
    [CmdletBinding()] param([string]$TaskName)
    if ($Sim.StartThrows) { throw 'No logged-on interactive user session for Mini pc' }
    if (-not $Sim.Task.Enabled) { return }
    if ((Test-TaskRunning) -and $Sim.Task.Settings.MultipleInstances -ne 'Parallel') { $Sim.IgnoredStarts++; return }
    if (Get-Procs 'TvLauncher') { return }
    $Sim.Starts++
    [void](Add-Proc 'TvLauncher' '"TvLauncher.exe"' -InJob)
    [void](Add-Proc 'msedgewebview2' '"msedgewebview2.exe" --user-data-dir="C:\Users\Mini pc\AppData\Local\TvLauncher\WebView2"' -InJob)
}
# What the logon trigger's repetition does each minute while Mini pc is signed in.
function Tick-Watchdog {
    $r = $Sim.Task.Triggers[0].Repetition
    if ($r -and $r.Interval -eq 'PT1M' -and $r.Duration -eq '') { Fake-StartTask -TaskName $Sim.Task.TaskName }
}
function Fake-Nothing { }
# Records which folder had its ACL reset, whether the flags were /reset /T /Q, and the version in it then.
function Fake-Icacls {
    $ok = ($args -contains '/reset') -and ($args -contains '/T') -and ($args -contains '/Q')
    $Sim.AclResets.Add(('{0}|{1}|{2}' -f $args[0], $ok, (Read-Marker $args[0])))
    if ($Sim.IcaclsFails) { Write-Output 'C:\Program Files\TvLauncher: Access is denied.'; $global:LASTEXITCODE = 5; return }
    $global:LASTEXITCODE = 0
}
function Acl-Fact {
    $last = @($Sim.AclResets)[-1]
    $f = if ($last) { $last.Split('|') } else { @('', '', '') }
    Fact 'acl-reset-on-install-folder' $(if ($f[0] -eq $Sim.PF -and $f[1] -eq 'True') { $f[2] } else { "wrong: $last" })
}

Set-Alias dotnet                Fake-Dotnet
Set-Alias ssh                   Fake-Ssh
Set-Alias scp                   Fake-Scp
Set-Alias Get-Process           Fake-GetProcess
Set-Alias Stop-Process          Fake-StopProcess
Set-Alias Get-CimInstance       Fake-GetCim
Set-Alias Move-Item             Fake-MoveItem
Set-Alias New-ScheduledTaskAction    Fake-NewAction
Set-Alias New-ScheduledTaskTrigger   Fake-NewTrigger
Set-Alias New-ScheduledTaskPrincipal Fake-NewPrincipal
Set-Alias New-ScheduledTaskSettingsSet Fake-NewSettings
Set-Alias Register-ScheduledTask Fake-RegisterTask
Set-Alias Get-ScheduledTask     Fake-GetTask
Set-Alias Disable-ScheduledTask Fake-DisableTask
Set-Alias Enable-ScheduledTask  Fake-EnableTask
Set-Alias Stop-ScheduledTask    Fake-StopTask
Set-Alias Start-ScheduledTask   Fake-StartTask
Set-Alias Start-Sleep           Fake-Nothing
Set-Alias icacls                Fake-Icacls

function Invoke-Target([switch]$Rollback, [switch]$Quiet) {
    Reset-Run
    if ($Quiet) { & $Target -PublishDir $Pub -ConfigSource $Cfg -DevConfig $DevCfg -Rollback:$Rollback *> $null }
    else { & $Target -PublishDir $Pub -ConfigSource $Cfg -DevConfig $DevCfg -Rollback:$Rollback }
    $LASTEXITCODE
}
function Read-Marker([string]$folder, [string]$file = 'TvLauncher.exe') {
    $p = Join-Path $folder $file
    if (Test-Path -LiteralPath $p) { (Get-Content -LiteralPath $p -Raw).Trim() -replace '^exe ', '' } else { 'absent' }
}
function Count-Calls([string]$kind) { @($Sim.Calls | Where-Object { $_ -eq $kind }).Count }
function Common-Facts {
    Fact 'install-version' (Read-Marker $Sim.PF)
    Fact 'previous' $(if (Test-Path -LiteralPath "$($Sim.PF).previous") { 'present' } else { 'absent' })
    Fact 'launcher-running-at-end' (YesNo ((Get-Procs 'TvLauncher').Count -ge 1))
    Fact 'task-started' $Sim.Starts
}
function Owner-Edits-Config { Set-Content -LiteralPath (Join-Path $Sim.PD 'launcher.json') -Value '{ "Location": { "Name": "OWNER-EDIT" } }' -Encoding ascii }
function Config-Edit-Kept { YesNo ((Get-Content -LiteralPath (Join-Path $Sim.PD 'launcher.json') -Raw) -match 'OWNER-EDIT') }
function Launcher-Stopped-Facts {
    # The launcher and its own WebView2 helper were stopped; Teams' WebView2, Edge (Disney+) and VacuumTube were not.
    $stoppedNames = @($Sim.Stops | ForEach-Object { ($_ -replace '^task:', '') -replace ':\d+$', '' })
    Fact 'launcher-and-its-webview-stopped' (YesNo (($stoppedNames -contains 'TvLauncher') -and (@($Sim.Procs | Where-Object { $_.Name -eq 'msedgewebview2' -and $_.CommandLine -like '*\TvLauncher\WebView2*' }).Count -le 1)))
    Fact 'other-processes-alive' (YesNo (@(102, 103, 104 | Where-Object { $id = $_; $Sim.Procs | Where-Object { $_.Id -eq $id } }).Count -eq 3))
}

$exit = 0
switch ($Scenario) {
    'first-deploy' {
        $exit = Invoke-Target
        Common-Facts
        Fact 'call-order' ($Sim.Calls -join ' ')
        Fact 'config' $(if ((Get-Content -LiteralPath (Join-Path $Sim.PD 'launcher.json') -Raw) -match '"Apps"') { 'created-from-repo' } else { 'wrong' })
        Fact 'staging-gone' (YesNo (-not (Test-Path -LiteralPath (Join-Path $Sim.PD 'staging'))))
        Fact 'bad-ssh-invocations' $Sim.BadSsh
        Fact 'encoded-length-ok' (YesNo ($Sim.MaxB64 -gt 0 -and $Sim.MaxB64 -lt 7000))
        $t = $Sim.Task
        Fact 'task-principal' $t.Principal.UserId
        Fact 'task-logon-type' $t.Principal.LogonType
        Fact 'task-trigger-user' $t.Triggers[0].UserId
        Fact 'task-restart' ("{0}x{1}min" -f $t.Settings.RestartCount, $t.Settings.RestartInterval.TotalMinutes)
        Fact 'task-time-limit' $t.Settings.ExecutionTimeLimit.TotalSeconds
        Fact 'task-multiple-instances' $t.Settings.MultipleInstances
        $rep = $t.Triggers[0].Repetition
        Fact 'task-watchdog' $(if ($rep) { '{0}/{1}' -f $rep.Interval, $(if ($rep.Duration -eq '') { 'indefinitely' } else { $rep.Duration }) } else { 'none' })
        Fact 'task-on-battery' (YesNo ($t.Settings.AllowStartIfOnBatteries -and $t.Settings.DontStopIfGoingOnBatteries))
        Fact 'task-action-exe' $(if ($t.Actions[0].Execute -eq (Join-Path $Sim.PF 'TvLauncher.exe')) { 'install-folder-exe' } else { $t.Actions[0].Execute })
        Fact 'task-enabled' (YesNo $t.Enabled)
        $all = $Sim.SshTexts -join "`n"
        Fact 'password-or-elevation-in-script' $(if ($all -match '(?i)-Password|SecureString|RunLevel\s+Highest|net(\.exe)?\s+user') { 'yes' } else { 'no' })
        Fact 'kills-by-name' ([regex]::Matches($all, '(?i)Stop-Process\s+-Name|taskkill|Get-Process\s+(msedge|VacuumTube)').Count)
        Acl-Fact
    }
    'redeploy-keeps-previous-and-config' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        Owner-Edits-Config
        $Sim.Version = 'v2'; $exit = Invoke-Target
        Common-Facts
        Fact 'previous-version' (Read-Marker "$($Sim.PF).previous")
        Fact 'config-kept-owner-edit' (Config-Edit-Kept)
        Fact 'swap-while-running' $Sim.SwapWhileRunning
        Launcher-Stopped-Facts
        Fact 'staging-gone' (YesNo (-not (Test-Path -LiteralPath (Join-Path $Sim.PD 'staging'))))
        Fact 'task-enabled' (YesNo $Sim.Task.Enabled)
    }
    'rollback' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        Owner-Edits-Config
        $Sim.Version = 'v2'; [void](Invoke-Target -Quiet)
        $exit = Invoke-Target -Rollback
        Common-Facts
        Acl-Fact
        Fact 'previous-version' (Read-Marker "$($Sim.PF).previous")
        Fact 'dotnet-calls' $Sim.Publishes
        Fact 'config-kept-owner-edit' (Config-Edit-Kept)
        Fact 'swap-while-running' $Sim.SwapWhileRunning
        Launcher-Stopped-Facts
        $second = Invoke-Target -Rollback
        Fact 'second-rollback-exit' $second
        Fact 'after-second-rollback' ("{0}/{1}" -f (Read-Marker $Sim.PF), (Read-Marker "$($Sim.PF).previous"))
    }
    'rollback-without-previous' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        $exit = Invoke-Target -Rollback
        Common-Facts
        Fact 'rollback-stops' $Sim.Stops.Count
    }
    'publish-fails-stops-before-copy' {
        $Sim.PublishFails = $true
        $exit = Invoke-Target
        Fact 'ssh-calls' (Count-Calls 'ssh')
        Fact 'scp-calls' (Count-Calls 'scp')
        Fact 'install-folder' $(if (Test-Path -LiteralPath $Sim.PF) { 'present' } else { 'absent' })
    }
    'apps-differ-on-dev-stops-before-build' {
        Set-Content -LiteralPath $DevCfg -Value '{ "Apps": [ { "Id": "plex", "Name": "Plex" } ] }' -Encoding ascii
        $exit = Invoke-Target
        Fact 'dotnet-calls' $Sim.Publishes
        Fact 'scp-calls' (Count-Calls 'scp')
        Fact 'install-folder' $(if (Test-Path -LiteralPath $Sim.PF) { 'present' } else { 'absent' })
    }
    'apps-differ-on-mini-pc-stops-before-build' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        Set-Content -LiteralPath (Join-Path $Sim.PD 'launcher.json') -Value '{ "Apps": [ { "Id": "plex", "Name": "Plex" } ] }' -Encoding ascii
        $Sim.Version = 'v2'
        $exit = Invoke-Target
        Common-Facts
        Fact 'dotnet-calls' $Sim.Publishes
        Fact 'stops-during-failed-run' $Sim.Stops.Count
    }
    'scp-fails-leaves-install-alone' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        $Sim.Version = 'v2'; $Sim.ScpFails = $true
        $exit = Invoke-Target
        Common-Facts
        Fact 'stops-during-failed-run' $Sim.Stops.Count
        Fact 'ssh-after-scp-failure' $Sim.SshAfterScpFailure
    }
    'wrong-machine' {
        $exit = Invoke-Target
        Fact 'scp-calls' (Count-Calls 'scp')
        Fact 'install-folder' $(if (Test-Path -LiteralPath $Sim.PF) { 'present' } else { 'absent' })
        Fact 'stops' $Sim.Stops.Count
    }
    'swap-fails-restarts-old-version' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        Owner-Edits-Config
        $Sim.Version = 'v2'; $Sim.SwapFails = $true
        $exit = Invoke-Target
        Common-Facts
        Fact 'config-kept-owner-edit' (Config-Edit-Kept)
    }
    'register-fails-reenables-and-starts' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        $Sim.Version = 'v2'; $Sim.RegisterFails = $true
        $exit = Invoke-Target
        Common-Facts
        Fact 'task-enabled' (YesNo $Sim.Task.Enabled)
        Launcher-Stopped-Facts
    }
    'rollback-start-fails-reenables-and-starts' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        $Sim.Version = 'v2'; [void](Invoke-Target -Quiet)
        $Sim.EnableFailsOnce = $true
        $exit = Invoke-Target -Rollback
        Common-Facts
        Fact 'previous-version' (Read-Marker "$($Sim.PF).previous")
        Fact 'task-enabled' (YesNo $Sim.Task.Enabled)
        Launcher-Stopped-Facts
    }
    'second-move-fails-restores-old-install' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        Owner-Edits-Config
        $Sim.Version = 'v2'; $Sim.SecondMoveFails = $true
        $exit = Invoke-Target
        Common-Facts
        Fact 'task-enabled' (YesNo $Sim.Task.Enabled)
        Fact 'config-kept-owner-edit' (Config-Edit-Kept)
        Launcher-Stopped-Facts
    }
    'rollback-second-move-fails-restores-install' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        $Sim.Version = 'v2'; [void](Invoke-Target -Quiet)
        $Sim.SecondMoveFails = $true
        $exit = Invoke-Target -Rollback
        Common-Facts
        Fact 'previous-version' (Read-Marker "$($Sim.PF).previous")
        Fact 'task-enabled' (YesNo $Sim.Task.Enabled)
        Launcher-Stopped-Facts
    }
    'deployed-but-not-running' {
        $Sim.StartThrows = $true
        $exit = Invoke-Target
        Common-Facts
        Fact 'task-enabled' (YesNo $Sim.Task.Enabled)
    }
    'acl-reset-fails-restarts' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        $Sim.Version = 'v2'; $Sim.IcaclsFails = $true
        $exit = Invoke-Target
        Common-Facts
        Fact 'task-enabled' (YesNo $Sim.Task.Enabled)
    }
    'redeploy-while-job-keeps-task-running' {
        $Sim.Version = 'v1'; [void](Invoke-Target -Quiet)
        # The launcher opened Disney+, so this Edge is in the task's job object.
        ($Sim.Procs | Where-Object { $_.Id -eq 103 }).InJob = $true
        $Sim.Version = 'v2'
        $exit = Invoke-Target
        Common-Facts
        Launcher-Stopped-Facts
        Fact 'ignored-starts' $Sim.IgnoredStarts
    }
    'crash-watchdog-restarts' {
        $Sim.Version = 'v1'; $exit = Invoke-Target -Quiet
        ($Sim.Procs | Where-Object { $_.Id -eq 103 }).InJob = $true
        Tick-Watchdog
        Fact 'watchdog-with-launcher-up-starts-another' (YesNo (@(Get-Procs 'TvLauncher').Count -gt 1))
        # The launcher crashes: it and its WebView2 helper end, Edge stays in the job.
        foreach ($p in @($Sim.Procs | Where-Object { $_.Name -eq 'TvLauncher' -or $_.CommandLine -like '*\TvLauncher\WebView2*' })) { [void]$Sim.Procs.Remove($p) }
        Fact 'after-crash-running' (YesNo ((Get-Procs 'TvLauncher').Count -ge 1))
        Tick-Watchdog
        Fact 'after-watchdog-running' (YesNo ((Get-Procs 'TvLauncher').Count -ge 1))
        Launcher-Stopped-Facts
    }
}
Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
exit $exit
