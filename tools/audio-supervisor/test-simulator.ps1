param(
    [string]$SimulatorPath = (Join-Path $PSScriptRoot '..\..\build\audio-supervisor\Release\cellport-audio-supervisor-sim.exe')
)

$resolved = (Resolve-Path -LiteralPath $SimulatorPath).Path
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = $resolved
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $start
if (-not $process.Start()) { throw 'Could not start protocol simulator.' }

function Read-Exact([System.IO.Stream]$stream, [int]$count) {
    $bytes = [byte[]]::new($count)
    $offset = 0
    while ($offset -lt $count) {
        $read = $stream.Read($bytes, $offset, $count - $offset)
        if ($read -le 0) { throw 'Unexpected EOF while reading simulator frame.' }
        $offset += $read
    }
    return ,$bytes
}

function Send-Frame([hashtable]$request) {
    $json = $request | ConvertTo-Json -Depth 8 -Compress
    $body = [System.Text.Encoding]::UTF8.GetBytes($json)
    $length = $body.Length
    [byte[]]$header = @([byte](($length -shr 24) -band 255), [byte](($length -shr 16) -band 255),
        [byte](($length -shr 8) -band 255), [byte]($length -band 255))
    $process.StandardInput.BaseStream.Write($header, 0, $header.Length)
    $process.StandardInput.BaseStream.Write($body, 0, $body.Length)
    $process.StandardInput.BaseStream.Flush()
    $responseHeader = Read-Exact $process.StandardOutput.BaseStream 4
    $responseLength = ([int]$responseHeader[0] -shl 24) -bor ([int]$responseHeader[1] -shl 16) -bor
        ([int]$responseHeader[2] -shl 8) -bor [int]$responseHeader[3]
    $responseBytes = Read-Exact $process.StandardOutput.BaseStream $responseLength
    return [System.Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
}

function New-Request([string]$version, [string]$id, [int]$sequence, [string]$session, [string]$owner, [string]$operation, [hashtable]$payload) {
    return @{
        messageType = 'request'; protocolVersion = $version; requestId = $id; requestSequence = $sequence
        bootId = 'synthetic-boot'; sessionId = $session; ownerToken = $owner; expectedGeneration = 0
        operation = $operation; payload = $payload
    }
}

try {
    $prepare = New-Request '1.0' 'sim-owner-a' 1 'sim-session-a' 'sim-token-a' 'prepare' @{
        profileId = 'simulator-only'; manifestDigest = '0000000000000000000000000000000000000000000000000000000000000000'
    }
    $first = Send-Frame $prepare
    if ($first.errorCode -ne 'NONE' -or $first.phase -ne 'standby_ready' -or $first.generation -ne 1) {
        throw "First simulated owner was not admitted: $($first | ConvertTo-Json -Compress)"
    }

    $competing = New-Request '1.0' 'sim-owner-b' 2 'sim-session-b' 'sim-token-b' 'prepare' @{
        profileId = 'simulator-only'; manifestDigest = '0000000000000000000000000000000000000000000000000000000000000000'
    }
    $second = Send-Frame $competing
    if ($second.errorCode -ne 'OPERATION_CONFLICT' -or $second.generation -ne 1) {
        throw "Competing owner was not rejected: $($second | ConvertTo-Json -Compress)"
    }

    $unknownMajor = New-Request '2.0' 'sim-v2' 3 $null $null 'inspect' @{}
    $version = Send-Frame $unknownMajor
    if ($version.errorCode -ne 'PROTOCOL_VERSION_UNSUPPORTED') {
        throw "Unknown major version was not rejected: $($version | ConvertTo-Json -Compress)"
    }
    'protocol simulator client: owner competition and major-version rejection passed'
}
finally {
    $process.StandardInput.Close()
    if (-not $process.HasExited) { $process.WaitForExit(3000) | Out-Null }
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
