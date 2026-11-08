# Cancel the queued built-in OpenSSH Server install on the mini PC and keep the
# winget sshd (C:\Program Files\OpenSSH) that is already working.
#
# Run over SSH from the dev PC, as one connection. The DISM
# removal runs in a one-off SYSTEM scheduled task, so a dropped SSH session
# cannot kill it halfway through servicing; this script waits for it, reports,
# and removes the task.
#
# Stops without changing anything if the running sshd is not the winget build.
# If the sshd service is gone afterwards, it recreates it with the settings the
# winget MSI gave it (read with sc.exe qc sshd on 2026-10-24: own process,
# LocalSystem, auto start, "C:\Program Files\OpenSSH\sshd.exe"). The MSI ships
# no install-sshd.ps1, unlike the zip release.

$ErrorActionPreference = 'Stop'
$cap        = 'OpenSSH.Server~~~~0.0.1.0'
$wingetDir  = 'C:\Program Files\OpenSSH'
$taskName   = 'tvos-cancel-builtin-openssh'
$log        = Join-Path $env:ProgramData 'tvos-cancel-builtin-openssh.log'
$cbs        = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing'

function Show-State($label) {
    $svc = Get-CimInstance Win32_Service -Filter "Name='sshd'"
    Write-Host ("[{0}] sshd: {1} | {2} | {3}" -f $label, $svc.State, $svc.StartMode, $svc.PathName)
    Get-ChildItem "$cbs\Packages" | Where-Object { $_.PSChildName -like 'OpenSSH-Server-Package~*' } | ForEach-Object {
        Write-Host ("[{0}] CBS {1} CurrentState=0x{2:X}" -f $label, $_.PSChildName, (Get-ItemProperty $_.PSPath).CurrentState)
    }
    Write-Host ("[{0}] built-in sshd.exe present: {1}; RebootPending: {2}" -f $label,
        (Test-Path "$env:WINDIR\System32\OpenSSH\sshd.exe"), (Test-Path "$cbs\RebootPending"))
}

# 1. Preflight (read-only)
Show-State 'before'
$path = (Get-CimInstance Win32_Service -Filter "Name='sshd'").PathName
if ($path -notlike "*$wingetDir\sshd.exe*") { throw "sshd is not the winget build ($path); nothing changed." }
if (-not (Test-Path "$wingetDir\sshd.exe")) { throw "$wingetDir\sshd.exe is missing; nothing changed." }

# 2. Remove the queued capability in a SYSTEM task that survives a dropped session
$cmd = "dism.exe /online /remove-capability /capabilityname:$cap /norestart /english > `"$log`" 2>&1 & echo EXITCODE %errorlevel% >> `"$log`""
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item $log -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $taskName -User 'SYSTEM' -RunLevel Highest -Force `
    -Action (New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c $cmd") | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Host 'DISM removal started as SYSTEM task; waiting up to 15 minutes...'

$deadline = (Get-Date).AddMinutes(15)
do {
    Start-Sleep -Seconds 15
    $done = (Test-Path $log) -and ((Get-Content $log -Raw) -match 'EXITCODE')
    Write-Host ("  {0:HH:mm:ss} task {1}" -f (Get-Date), (Get-ScheduledTask -TaskName $taskName).State)
} until ($done -or (Get-Date) -gt $deadline)

if (-not $done) {
    Write-Host "Still running after 15 minutes. Left the task running; its log is $log. Check again later."
    exit 2
}
Write-Host '--- DISM log'
Get-Content $log | Where-Object { $_.Trim() } | ForEach-Object { Write-Host "  $_" }
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false

# 3. Safety net: the winget sshd must still be registered and running
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    Write-Host 'sshd service missing after removal; recreating it for the winget build.'
    New-Service -Name sshd -BinaryPathName "`"$wingetDir\sshd.exe`"" -DisplayName 'OpenSSH SSH Server' `
        -StartupType Automatic -Description 'OpenSSH is a connectivity tool for remote login that uses the SSH protocol.' | Out-Null
}
if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }
Show-State 'after'
Write-Host 'DONE'
