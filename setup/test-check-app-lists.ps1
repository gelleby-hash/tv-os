# Tests check-app-lists.ps1 on any PC with fixture configs; the mini PC is never contacted (every
# scenario passes -MiniPcConfig). Each scenario runs in its own process, because the script calls exit.
#
#   powershell -ExecutionPolicy Bypass -File .\test-check-app-lists.ps1
#
# Exits non-zero if any scenario does not behave as expected.

$Target = Join-Path $PSScriptRoot 'check-app-lists.ps1'
$Work = Join-Path $env:TEMP ("tvos-check-apps-{0}" -f $PID)
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Path $Work | Out-Null

$youtube = '{ "Id": "youtube", "Name": "YouTube", "Url": "https://www.youtube.com/tv", "PhoneMic": true }'
$disney = '{ "Id": "disney", "Name": "Disney+", "BoostGain": 4 }'
$c4tv = '{ "Id": "tv", "Name": "TV", "Url": "https://c4.example/tv" }'
$c4web = '{ "Id": "web", "Name": "Website", "Url": "https://www.channel4.com" }'
function Channel4([string[]]$versions) { '{ "Id": "channel4", "Name": "Channel 4", "Versions": [ ' + ($versions -join ', ') + ' ] }' }
function Config([string[]]$apps, [string]$extra = '"Locale": "en-GB"') { '{ "Apps": [ ' + ($apps -join ', ') + ' ], ' + $extra + ' }' }
$all = Config @($youtube, $disney, (Channel4 @($c4tv, $c4web)))

# name = repository, dev PC and mini PC config text ($null: no such file), expected exit, text that must and must not appear
$Scenarios = [ordered]@{
    'match' = @{ Repo = $all; Dev = $all; Mini = $all; Exit = 0
        Must = @('The app lists match', 'youtube, disney, channel4 (tv, web)'); MustNot = @('DIFFER') }
    'machine-settings-may-differ' = @{ Repo = $all; Dev = (Config @($youtube, $disney, (Channel4 @($c4tv, $c4web))) '"AllowClose": true, "Location": { "Name": "X" }')
        Mini = (Config @($youtube, $disney, (Channel4 @($c4tv, $c4web))) '"PhoneRemote": { "Enabled": true }'); Exit = 0
        Must = @('The app lists match'); MustNot = @('DIFFER') }
    'byte-order-mark-and-layout' = @{ Repo = $all; Dev = ([char]0xFEFF + ($all -replace ', ', ",`r`n    ")); Mini = $all; Exit = 0
        Must = @('The app lists match'); MustNot = @('DIFFER') }
    'app-missing-on-dev' = @{ Repo = $all; Dev = (Config @($youtube, $disney)); Mini = $all; Exit = 1
        Must = @('DIFFER: channel4 is missing on the dev PC'); MustNot = @('The app lists match') }
    'extra-app-on-mini' = @{ Repo = $all; Dev = $all; Mini = (Config @($youtube, $disney, (Channel4 @($c4tv, $c4web)), '{ "Id": "plex", "Name": "Plex" }')); Exit = 1
        Must = @('DIFFER: plex is on the mini PC but not in the repository'); MustNot = @('The app lists match') }
    'version-missing-on-dev' = @{ Repo = $all; Dev = (Config @($youtube, $disney, (Channel4 @($c4tv)))); Mini = $all; Exit = 1
        Must = @('DIFFER: channel4/web is missing on the dev PC'); MustNot = @('The app lists match') }
    'field-missing-on-dev' = @{ Repo = $all; Dev = (Config @('{ "Id": "youtube", "Name": "YouTube", "Url": "https://www.youtube.com/tv" }', $disney, (Channel4 @($c4tv, $c4web)))); Mini = $all; Exit = 1
        Must = @('DIFFER: youtube PhoneMic: repository true, dev PC (none)'); MustNot = @('The app lists match') }
    'version-field-differs-on-mini' = @{ Repo = $all; Dev = $all; Mini = (Config @($youtube, $disney, (Channel4 @('{ "Id": "tv", "Name": "TV", "Url": "https://c4.example/old" }', $c4web)))); Exit = 1
        Must = @('DIFFER: channel4/tv Url: repository "https://c4.example/tv", mini PC "https://c4.example/old"'); MustNot = @('The app lists match') }
    'order-differs' = @{ Repo = $all; Dev = (Config @($disney, $youtube, (Channel4 @($c4tv, $c4web)))); Mini = $all; Exit = 1
        Must = @('DIFFER: the dev PC lists the apps in another order: disney, youtube, channel4'); MustNot = @('The app lists match') }
    'mini-pc-has-no-config-yet' = @{ Repo = $all; Dev = $all; Mini = $null; Exit = 0
        Must = @('the deploy creates it from the repository', 'The app lists match'); MustNot = @('DIFFER') }
    'no-apps-key-is-no-apps' = @{ Repo = '{ "Apps": [] }'; Dev = '{ "Apps": [] }'; Mini = '{ "Location": { "Name": "OWNER-EDIT" } }'; Exit = 0
        Must = @('The app lists match', '(no apps)'); MustNot = @('DIFFER') }
    'dev-config-missing' = @{ Repo = $all; Dev = $null; Mini = $all; Exit = 1
        Must = @('FAILED', 'dev PC', 'not found'); MustNot = @('The app lists match') }
    'unreadable-json' = @{ Repo = $all; Dev = '{ "Apps": [ '; Mini = $all; Exit = 1
        Must = @('FAILED', 'could not read the dev PC'); MustNot = @('The app lists match') }
}

$failed = 0
foreach ($name in $Scenarios.Keys) {
    $s = $Scenarios[$name]
    $paths = @{}
    foreach ($k in 'Repo', 'Dev', 'Mini') {
        $paths[$k] = Join-Path $Work "$name-$k.json"
        if ($null -ne $s[$k]) { [IO.File]::WriteAllText($paths[$k], $s[$k], (New-Object Text.UTF8Encoding($false))) }
    }
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $Target -RepoConfig $paths.Repo -DevConfig $paths.Dev -MiniPcConfig $paths.Mini 2>&1 | Out-String
    $code = $LASTEXITCODE
    $problems = @()
    if ($code -ne $s.Exit) { $problems += "exit $code, expected $($s.Exit)" }
    foreach ($t in $s.Must)    { if ($out -cnotmatch [regex]::Escape($t)) { $problems += "missing '$t'" } }
    foreach ($t in $s.MustNot) { if ($out -cmatch    [regex]::Escape($t)) { $problems += "unexpected '$t'" } }
    if ($problems) {
        $failed++
        Write-Host ("FAIL {0}: {1}" -f $name, ($problems -join '; '))
        Write-Host ($out -replace '(?m)^', '     | ')
    } else {
        Write-Host ("pass {0}" -f $name)
    }
}
Remove-Item -LiteralPath $Work -Recurse -Force
Write-Host ("{0} of {1} scenarios passed" -f ($Scenarios.Count - $failed), $Scenarios.Count)
exit [int]($failed -gt 0)
