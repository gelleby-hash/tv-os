# Builds the TV launcher and installs it on the mini PC (NUCBOX_M6ULTRA) over `ssh tvbox`.
# Run on the DEV PC, from anywhere:
#   powershell -ExecutionPolicy Bypass -File .\setup\deploy-launcher.ps1
#   powershell -ExecutionPolicy Bypass -File .\setup\deploy-launcher.ps1 -Rollback
#
# Deploy:
#   1. Runs check-app-lists.ps1: the apps in the repository's launcher.json, the dev PC's
#      dev-launcher.json (-DevConfig) and the mini PC's launcher.json must match in full (owner,
#      2026-11-02). If they differ, or one cannot be read, nothing is built or sent.
#   2. dotnet publish (Release, self-contained win-x64) into launcher\artifacts\publish.
#      If the build fails, nothing is sent to the mini PC.
#   3. Copies the publish folder and launcher.json to C:\ProgramData\TvLauncher\staging
#      on the mini PC. Nothing live changes during the copy.
#   4. Stops the running launcher and its own WebView2 helper processes (the home page's and
#      each app's, all using data folders under %LOCALAPPDATA%\TvLauncher\WebView2*; never
#      Edge itself), keeps the old install as C:\Program Files\TvLauncher.previous (one
#      generation; an older .previous is replaced), and moves the new one into place.
#      A moved folder keeps the ACL it had in ProgramData staging, so the install folder's
#      ACL is reset to inherit from Program Files (icacls /reset), here and on -Rollback.
#      C:\ProgramData\TvLauncher\launcher.json is created from the repo copy only if it
#      is absent, so edits made on the mini PC are never overwritten. A new setting added
#      to the repo's launcher.json therefore has to be added there by hand.
#   5. Registers the scheduled task 'TvLauncher' (starts at logon of Mini pc, in that
#      session) and starts it. The account has no password and keeps none: the task uses
#      -LogonType Interactive, which needs none. The logon trigger repeats every minute,
#      indefinitely, with -MultipleInstances Parallel: a watchdog that brings the launcher
#      back after a crash. Each repeat starts a new copy, which exits at once when a
#      launcher is already running (its single-instance check comes first). Parallel,
#      because child processes (the launcher's WebView2 helpers; Edge and VacuumTube in the
#      first build, before the apps moved inside the launcher) run in the task's job object and keep the task
#      "Running" after the launcher itself has gone, and IgnoreNew would then skip every
#      start, including the one after a deploy. Restart-on-failure only covers a task
#      that fails to start, not a launcher that exits with an error.
#   6. Checks the installed file count and the SHA-256 of TvLauncher.exe against the build.
#
# -Rollback  stops the launcher, swaps C:\Program Files\TvLauncher and TvLauncher.previous
#            (so a second -Rollback goes forward again), and starts it. Config is untouched.
#
# Exit codes: 0 done, 1 failed, 2 installed but the launcher is not running 5 s after the
# start (the script prints what Task Scheduler said; it starts at the next sign-in).
#
# Every remote step is a short PowerShell script sent with -EncodedCommand, because plain
# quoting through ssh mangles multi-line commands and the paths contain spaces. Files go
# to a space-free staging folder first (scp to "C:\Program Files\..." needs fragile
# quoting) and are moved into place by the remote script.
#
# Tested without the mini PC by test-deploy-launcher.ps1.

param(
    [switch]$Rollback,
    [string]$PublishDir = (Join-Path $PSScriptRoot '..\launcher\artifacts\publish'),
    [string]$ConfigSource = (Join-Path $PSScriptRoot '..\launcher\src\TvLauncher\launcher.json'),
    [string]$Project = (Join-Path $PSScriptRoot '..\launcher\src\TvLauncher'),
    [string]$DevConfig = (Join-Path $env:LOCALAPPDATA 'TvLauncher\dev-launcher.json')
)

$ErrorActionPreference = 'Stop'
$PublishDir = [IO.Path]::GetFullPath($PublishDir)
$ConfigSource = [IO.Path]::GetFullPath($ConfigSource)
$Project = [IO.Path]::GetFullPath($Project)

# ---- the remote scripts (kept short: the encoded form travels on the ssh command line) ----

# Resets the install folder's ACL to what Program Files gives it.
$ResetAcl = @'
& icacls $I /reset /T /Q | Out-Null
if($LASTEXITCODE -ne 0){throw "icacls /reset on $I failed (exit $LASTEXITCODE)"}
'@

# Runs first in every remote script. $I install folder, $P its previous version, $D data folder, $S staging.
$Head = @'
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'NUCBOX_M6ULTRA'){throw "Wrong machine: $env:COMPUTERNAME"}
$I='C:\Program Files\TvLauncher';$P="$I.previous";$D='C:\ProgramData\TvLauncher';$S="$D\staging"
'@

# Stops only TvLauncher and the WebView2 processes that use its own data folder. The task is
# disabled first so its one-minute watchdog does not bring the launcher back mid-swap.
# Stop-ScheduledTask is deliberately NOT used: Task Scheduler ends the task's whole process tree,
# and only the launcher's own processes should go.
$StopLauncher = @'
if(Get-ScheduledTask -TaskName TvLauncher -ErrorAction SilentlyContinue){Disable-ScheduledTask -TaskName TvLauncher | Out-Null}
Get-Process TvLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
foreach($w in (Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'")){if($w.CommandLine -like '*\TvLauncher\WebView2*'){Stop-Process -Id $w.ProcessId -Force -ErrorAction SilentlyContinue}}
for($n=0;$n -lt 30 -and (Get-Process TvLauncher -ErrorAction SilentlyContinue);$n++){Start-Sleep 1}
if(Get-Process TvLauncher -ErrorAction SilentlyContinue){throw 'TvLauncher is still running'}
'@

$Report = @'
$h=(Get-FileHash "$I\TvLauncher.exe").Hash
"FILES=$((Get-ChildItem $I -Recurse -File).Count)"
"HASH=$h"
'@

$PrepareBody = @'
New-Item -ItemType Directory -Path $D -Force | Out-Null
if(Test-Path $S){Remove-Item $S -Recurse -Force}
New-Item -ItemType Directory -Path $S -Force | Out-Null
"HOST=$env:COMPUTERNAME"
'@

$InstallBody = @"
if(-not(Test-Path "`$S\publish\TvLauncher.exe")){throw 'Nothing staged'}
`$C="`$D\launcher.json"
if(Test-Path `$C){`$cfg='kept'}else{Copy-Item "`$S\launcher.json" `$C;`$cfg='created'}
$StopLauncher
`$moved=`$false
try{
if(Test-Path `$I){if(Test-Path `$P){Remove-Item `$P -Recurse -Force};Move-Item `$I `$P;`$moved=`$true}
Move-Item "`$S\publish" `$I
}catch{if(`$moved -and -not(Test-Path `$I)){Move-Item `$P `$I};throw}
$ResetAcl
Remove-Item `$S -Recurse -Force
"CONFIG=`$cfg"
$Report
"@

$RollbackBody = @"
if(-not(Test-Path "`$P\TvLauncher.exe")){throw 'No previous version to roll back to'}
$StopLauncher
`$X="`$I.swap"
if(Test-Path `$X){Remove-Item `$X -Recurse -Force}
Move-Item `$I `$X
try{Move-Item `$P `$I}catch{Move-Item `$X `$I;throw}
Move-Item `$X `$P
$ResetAcl
$Report
"@

$RegisterBody = @'
$u='NUCBOX_M6ULTRA\Mini pc'
$a=New-ScheduledTaskAction -Execute "$I\TvLauncher.exe" -WorkingDirectory $I
$t=New-ScheduledTaskTrigger -AtLogOn -User $u
$t.Repetition=(New-ScheduledTaskTrigger -Once -At '2000-01-01' -RepetitionInterval (New-TimeSpan -Minutes 1)).Repetition
$p=New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Limited
$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances Parallel
Register-ScheduledTask -TaskName TvLauncher -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null
$r=Get-ScheduledTask -TaskName TvLauncher;$w=$r.Triggers[0].Repetition
if($r.Principal.LogonType -ne 'Interactive' -or $r.Principal.UserId -notlike '*Mini pc' -or $r.Settings.RestartCount -ne 3 -or $r.Settings.MultipleInstances -ne 'Parallel' -or $w.Interval -ne 'PT1M' -or $w.Duration){throw 'The task did not register as intended'}
"TASK=$($r.TaskName) $($r.Principal.UserId) $($r.Principal.LogonType) restarts=$($r.Settings.RestartCount) instances=$($r.Settings.MultipleInstances) watchdog=$($w.Interval)"
'@

$StartBody = @'
Enable-ScheduledTask -TaskName TvLauncher | Out-Null
try{Start-ScheduledTask -TaskName TvLauncher}catch{Write-Output ('START-ERROR: '+$_.Exception.Message)}
Start-Sleep 5
"RUNNING=$([bool](Get-Process TvLauncher -ErrorAction SilentlyContinue))"
'@

# ---- helpers ----

function Invoke-Remote([string]$Name, [string]$Body) {
    $text = "try{`n$Head`n$Body`n}catch{Write-Output ('REMOTE-ERROR: '+`$_.Exception.Message);throw}"
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($text))
    if ($b64.Length -gt 7000) { throw "Remote step '$Name' is too long to send over ssh ($($b64.Length) characters)" }
    Write-Host "--- mini PC: $Name"
    $lines = @(& ssh -o BatchMode=yes tvbox powershell -NoProfile -NonInteractive -EncodedCommand $b64)
    $code = $LASTEXITCODE
    foreach ($l in $lines) { Write-Host "    $l" }
    if ($code -ne 0) { throw "Remote step '$Name' failed (ssh exit $code)" }
    $lines
}

function Get-Value([string[]]$Lines, [string]$Key) {
    $m = @($Lines | Where-Object { $_ -like "$Key=*" } | Select-Object -Last 1)
    if (-not $m) { throw "The mini PC did not report $Key" }
    $m[0].Substring($Key.Length + 1).Trim()
}

function Copy-ToMiniPc([string]$LocalParent, [string]$Leaf, [string]$RemoteDir, [switch]$Recurse) {
    # Relative source from its own folder: scp can read "D:\..." as host "D".
    Push-Location -LiteralPath $LocalParent
    try {
        $flags = @('-o', 'BatchMode=yes') + $(if ($Recurse) { '-r' })
        & scp @flags $Leaf "tvbox:$RemoteDir"
        if ($LASTEXITCODE -ne 0) { throw "scp of $Leaf failed (exit $LASTEXITCODE)" }
    } finally { Pop-Location }
}

# Set once the launcher may have been stopped (the install or rollback step has been sent).
$script:LauncherTouched = $false

function Invoke-Swap([string]$Name, [string]$Body) {
    $script:LauncherTouched = $true
    Invoke-Remote $Name $Body
}

# Any failure after the stop must put the launcher back on whatever is installed (and re-enable
# the task the stop disabled): a failed deploy must not leave the family's TV on a bare desktop,
# now or at the next sign-in.
function Restore-Launcher {
    Write-Host 'Failed after the launcher was stopped; trying to re-enable the task and start the version that was in place.'
    try { [void](Invoke-Remote 'start the launcher again' $StartBody) } catch { Write-Host "Could not restart it either: $($_.Exception.Message)" }
}

function Complete-Start([string[]]$Started) {
    if ((Get-Value $Started 'RUNNING') -ne 'True') {
        Write-Host 'The launcher is installed but is NOT running 5 s after the start.'
        $err = @($Started | Where-Object { $_ -like 'START-ERROR: *' })
        if ($err) { Write-Host ("Task Scheduler said: {0}" -f $err[0].Substring(13)) }
        else { Write-Host 'Task Scheduler reported no error, so the launcher may have exited: see %LOCALAPPDATA%\TvLauncher\launcher.log of Mini pc.' }
        Write-Host 'The task starts it at the next sign-in of Mini pc, and while Mini pc is signed in its watchdog tries again every minute.'
        exit 2
    }
}

# ---- main ----

try {
    if ($Rollback) {
        [void](Invoke-Remote 'prepare' $PrepareBody)
        $out = Invoke-Swap 'swap back to the previous version' $RollbackBody
        $started = Invoke-Remote 'start the launcher' $StartBody
        Complete-Start $started
        Write-Host ("Rolled back. Installed exe SHA-256 {0}" -f (Get-Value $out 'HASH'))
        Write-Host 'DONE'
        exit 0
    }

    if ((Split-Path $PublishDir -Leaf) -ne 'publish') { throw "PublishDir must end in 'publish' (it is cleared, and the copy relies on the name): $PublishDir" }
    if (-not (Test-Path -LiteralPath $ConfigSource)) { throw "Config source not found: $ConfigSource" }

    Write-Host '--- the apps in the repository, the dev PC and the mini PC'
    & (Join-Path $PSScriptRoot 'check-app-lists.ps1') -RepoConfig $ConfigSource -DevConfig $DevConfig
    if ($LASTEXITCODE -ne 0) { throw 'the app check did not pass (see above); nothing was built or sent to the mini PC' }

    Write-Host '--- dev PC: dotnet publish'
    if (Test-Path -LiteralPath $PublishDir) { Remove-Item -LiteralPath $PublishDir -Recurse -Force }
    & dotnet publish $Project -c Release -r win-x64 --self-contained true -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed (dotnet exit $LASTEXITCODE); nothing was sent to the mini PC" }
    $exe = Join-Path $PublishDir 'TvLauncher.exe'
    if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath (Join-Path $PublishDir 'web\index.html'))) {
        throw "publish failed: TvLauncher.exe or web\index.html missing in $PublishDir; nothing was sent to the mini PC"
    }
    $localFiles = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File).Count
    $localHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    Write-Host ("Built {0} files, TvLauncher.exe SHA-256 {1}" -f $localFiles, $localHash)

    [void](Invoke-Remote 'prepare the staging folder' $PrepareBody)
    Write-Host '--- copying to the mini PC (self-contained, so large)'
    Copy-ToMiniPc (Split-Path $PublishDir -Parent) 'publish' 'C:/ProgramData/TvLauncher/staging/' -Recurse
    Copy-ToMiniPc (Split-Path $ConfigSource -Parent) (Split-Path $ConfigSource -Leaf) 'C:/ProgramData/TvLauncher/staging/'

    $out = Invoke-Swap 'stop the launcher and move the new version in' $InstallBody
    [void](Invoke-Remote 'register the scheduled task' $RegisterBody)
    $started = Invoke-Remote 'start the launcher' $StartBody

    $remoteFiles = [int](Get-Value $out 'FILES')
    $remoteHash = Get-Value $out 'HASH'
    if ($remoteFiles -ne $localFiles) { throw "Installed $remoteFiles files but built $localFiles" }
    if ($remoteHash -ne $localHash) { throw "Installed TvLauncher.exe hash $remoteHash differs from the build $localHash" }
    Write-Host ("Verified: {0} files and the exe hash match the build. Config: {1}." -f $remoteFiles, (Get-Value $out 'CONFIG'))

    Complete-Start $started
    Write-Host 'DONE'
    exit 0
} catch {
    Write-Host "FAILED: $($_.Exception.Message)"
    if ($script:LauncherTouched) { Restore-Launcher }
    exit 1
}
