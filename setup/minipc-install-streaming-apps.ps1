# Install the streaming apps into the family's "Mini pc" account from the dev PC,
# without a password, by running a one-off scheduled task AS the signed-in user.
#
# Run over SSH (admin) as one connection. Requires "Mini pc" to be signed in on
# the TV. Installs, for that account only:
#   - Disney+ (9NXQXXLFST89) from the Microsoft Store via winget, accepting the
#     Store source and package agreements for that account. (Netflix and Prime
#     Video skipped: no logins for them, 2026-10-24.)
#   - VacuumTube v2.0.1 from https://github.com/shy1132/VacuumTube/releases
#     (VacuumTube-Setup.exe, 227.7 MB), only if its SHA-256 matches the digest
#     GitHub published for that file, installed silently (/S, per-user)
# Signing in to each service is left for the user at the TV.
# The task, the inner script and the downloaded installer are removed at the end;
# the log stays at C:\Users\Mini pc\AppData\Local\Temp\tvos-install-apps.log.

$ErrorActionPreference = 'Stop'
$user      = "$env:COMPUTERNAME\Mini pc"
$taskName  = 'tvos-install-streaming-apps'
$userDir   = 'C:\Users\Mini pc'
$inner     = Join-Path $userDir 'AppData\Local\Temp\tvos-install-apps.ps1'
$log       = Join-Path $userDir 'AppData\Local\Temp\tvos-install-apps.log'

$console = (qwinsta console 2>$null | Select-String 'Mini pc')
if (-not $console) { throw '"Mini pc" is not signed in on the TV console; nothing changed.' }

$innerText = @'
$ErrorActionPreference = 'Continue'
$log = Join-Path $env:LOCALAPPDATA 'Temp\tvos-install-apps.log'
function Log($m) { Add-Content -Path $log -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $m) }
Log "start as $env:USERNAME"
$out = winget install --id 9NXQXXLFST89 --source msstore --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 | Out-String
Log ("Disney+: winget exit {0}; {1}" -f $LASTEXITCODE, (($out -split "`n" | Where-Object { $_ -match 'Successfully|already installed|No newer|failed|error' }) -join ' | ').Trim())
$url = 'https://github.com/shy1132/VacuumTube/releases/download/v2.0.1/VacuumTube-Setup.exe'
$sha = '25F8F5E631175392E5D721FCEDDA7C1B49775E59D25453D4E99AB468BFA02875'
$exe = Join-Path $env:LOCALAPPDATA 'Temp\VacuumTube-Setup.exe'
try {
    Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing
    $got = (Get-FileHash $exe -Algorithm SHA256).Hash
    if ($got -ne $sha) { Log "VacuumTube: SHA-256 MISMATCH ($got); not installed" }
    else {
        $p = Start-Process $exe -ArgumentList '/S' -Wait -PassThru
        Log ("VacuumTube: hash OK, installer exit {0}" -f $p.ExitCode)
    }
} catch { Log ("VacuumTube: failed: {0}" -f $_.Exception.Message) }
Remove-Item $exe -ErrorAction SilentlyContinue
Log 'DONE'
'@

Set-Content -Path $inner -Value $innerText -Encoding utf8
Remove-Item $log -ErrorAction SilentlyContinue
icacls $inner /grant "${user}:R" | Out-Null

$action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$inner`""
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Output 'Install task started in the "Mini pc" session; waiting up to 25 minutes...'

$deadline = (Get-Date).AddMinutes(25)
do {
    Start-Sleep -Seconds 20
    $done = (Test-Path $log) -and ((Get-Content $log -Raw) -match 'DONE')
} until ($done -or (Get-Date) -gt $deadline)

if (Test-Path $log) { Get-Content $log | ForEach-Object { Write-Output "  $_" } } else { Write-Output '  (no log written)' }
if (-not $done) { Write-Output 'Not finished after 25 minutes; task left running. Check the log later.'; exit 2 }

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
Remove-Item $inner -ErrorAction SilentlyContinue
Write-Output '--- installed for Mini pc'
$sid = (Get-CimInstance Win32_UserAccount -Filter "Name='Mini pc'").SID
Get-AppxPackage -User $sid | Where-Object { $_.Name -match 'Disney' } | ForEach-Object { Write-Output ("  store: {0} {1}" -f $_.Name, $_.Version) }
Get-ChildItem "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Uninstall" -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath } | Where-Object { $_.DisplayName -match 'VacuumTube' } |
    ForEach-Object { Write-Output ("  app:   {0} {1}" -f $_.DisplayName, $_.DisplayVersion) }
Write-Output 'DONE'
