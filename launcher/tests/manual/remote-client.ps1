# Manual check of the phone remote's listener: plays the phone's part from a PowerShell window.
# Windows PowerShell 5.1, on the dev PC:
#   powershell -ExecutionPolicy Bypass -File launcher\tests\manual\remote-client.ps1 -Address 127.0.0.1:47800 -Key up
#   powershell -ExecutionPolicy Bypass -File launcher\tests\manual\remote-client.ps1 -Address 127.0.0.1:47800 -Key right -Count 5
#
# It connects to ws://<Address>/remote/v1, prints the launcher's first message (the state: which app is in
# front), sends -Count presses of -Key (one press each, key down and up together, as the app sends them),
# prints every message the launcher sends meanwhile, and closes. The presses are REAL key presses on the PC
# running the launcher: whatever window is in front gets them.
#
# ClientWebSocket sends no Origin header, as the app does not, so the listener accepts it (it refuses
# browsers by their Origin).
#
# Exit codes: 0 done, 1 could not connect or the connection failed.

param(
    [Parameter(Mandatory = $true)]
    [string]$Address,

    # The protocol's names, matched exactly: the listener refuses "Up" or "OK".
    [Parameter(Mandatory = $true)]
    [ValidateSet('up', 'down', 'left', 'right', 'ok', 'back', 'home', 'playPause', 'volumeUp', 'volumeDown', IgnoreCase = $false)]
    [string]$Key,

    [ValidateRange(1, 100)]
    [int]$Count = 1,

    # The gap between presses. The app repeats a held arrow at a steady rate; this stands in for it.
    [ValidateRange(0, 5000)]
    [int]$IntervalMs = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$uri = [Uri]"ws://$Address/remote/v1"
$socket = New-Object System.Net.WebSockets.ClientWebSocket
$buffer = New-Object byte[] 4096
$pending = $null    # the one receive in flight; a WebSocket allows one at a time
$received = New-Object System.Collections.Generic.List[byte]

function Write-Line([string]$text) {
    Write-Host ("{0:HH:mm:ss.fff}  {1}" -f (Get-Date), $text)
}

# Prints each message that arrives within $milliseconds, then returns. False once the connection has ended.
function Receive-Messages([int]$milliseconds) {
    $until = (Get-Date).AddMilliseconds($milliseconds)
    while ($true) {
        if ($null -eq $script:pending) {
            $segment = New-Object System.ArraySegment[byte] -ArgumentList (, $buffer)
            $script:pending = $socket.ReceiveAsync($segment, [Threading.CancellationToken]::None)
        }
        $left = [int][Math]::Max(0, ($until - (Get-Date)).TotalMilliseconds)
        if (-not $script:pending.Wait($left)) {
            return $true
        }
        $result = $script:pending.Result
        $script:pending = $null
        if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
            Write-Line "closed by the launcher: $($result.CloseStatus) $($result.CloseStatusDescription)"
            return $false
        }
        for ($i = 0; $i -lt $result.Count; $i++) { $received.Add($buffer[$i]) }
        if ($result.EndOfMessage) {
            if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Text) {
                Write-Line ("launcher: " + [Text.Encoding]::UTF8.GetString($received.ToArray()))
            }
            else {
                Write-Line "launcher: binary frame of $($received.Count) bytes"
            }
            $received.Clear()
        }
    }
}

function Send-Text([string]$text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $segment = New-Object System.ArraySegment[byte] -ArgumentList (, $bytes)
    $socket.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
}

try {
    try {
        $socket.ConnectAsync($uri, [Threading.CancellationToken]::None).Wait(5000) | Out-Null
    }
    catch {
        Write-Line "could not connect to ${uri}: $($_.Exception.GetBaseException().Message)"
        exit 1
    }
    if ($socket.State -ne [System.Net.WebSockets.WebSocketState]::Open) {
        Write-Line "could not connect to ${uri} within 5 s (state $($socket.State))"
        exit 1
    }
    Write-Line "connected to $uri"

    # The launcher sends the state as soon as a phone connects.
    $open = Receive-Messages 1000

    $press = '{"v":1,"type":"press","key":"' + $Key + '"}'
    for ($n = 1; $open -and $n -le $Count; $n++) {
        Send-Text $press
        Write-Line "sent $n of ${Count}: $press"
        $open = Receive-Messages $IntervalMs
    }

    # Room for the state after a press that opened an app or went home.
    if ($open) {
        $open = Receive-Messages 1500
    }

    if ($open) {
        # Our close goes first; the pending receive then gets the launcher's answer.
        $socket.CloseOutputAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done', [Threading.CancellationToken]::None).Wait()
        Receive-Messages 2000 | Out-Null
    }
    Write-Line "done ($($socket.State))"
    exit 0
}
catch {
    Write-Line "connection failed: $($_.Exception.GetBaseException().Message)"
    exit 1
}
finally {
    $socket.Dispose()
}
