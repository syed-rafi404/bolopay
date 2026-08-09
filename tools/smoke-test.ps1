# Smoke-tests the pipeline endpoint with a synthetic WAV.
# Verifies: happy path, rate limiting, oversize rejection, bad content type.

$ErrorActionPreference = 'Stop'
$base = if ($args[0]) { $args[0] } else { 'http://localhost:5230' }
$url  = "$base/api/process-voice-command"

Add-Type -AssemblyName System.Net.Http

function New-WavBytes {
    param([int]$Seconds = 3, [int]$SampleRate = 16000)

    $samples   = $Seconds * $SampleRate
    $dataBytes = $samples * 2
    $ms        = New-Object System.IO.MemoryStream
    $bw        = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([char[]]'RIFF');            $bw.Write([int]($dataBytes + 36))
    $bw.Write([char[]]'WAVE')
    $bw.Write([char[]]'fmt ');            $bw.Write([int]16)
    $bw.Write([int16]1);                  $bw.Write([int16]1)
    $bw.Write([int]$SampleRate);          $bw.Write([int]($SampleRate * 2))
    $bw.Write([int16]2);                  $bw.Write([int16]16)
    $bw.Write([char[]]'data');            $bw.Write([int]$dataBytes)

    # Quiet 440Hz tone so the payload isn't pure silence.
    for ($i = 0; $i -lt $samples; $i++) {
        $v = [int16](3000 * [Math]::Sin(2 * [Math]::PI * 440 * $i / $SampleRate))
        $bw.Write($v)
    }

    $bw.Flush()
    return $ms.ToArray()
}

function Invoke-Pipeline {
    param([byte[]]$Bytes, [string]$ContentType = 'audio/wav', [string]$FileName = 'command.wav')

    $client  = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $form    = [System.Net.Http.MultipartFormDataContent]::new()
    $part    = [System.Net.Http.ByteArrayContent]::new($Bytes)
    $part.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($ContentType)
    $form.Add($part, 'audio', $FileName)

    $resp = $client.PostAsync($url, $form).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $client.Dispose()

    return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $body }
}

$wav = New-WavBytes -Seconds 3
Write-Host "Synthetic WAV: $($wav.Length) bytes`n"

Write-Host '=== Happy path (x4, exercises stub rotation) ==='
for ($i = 1; $i -le 4; $i++) {
    $r = Invoke-Pipeline -Bytes $wav
    Write-Host "[$i] HTTP $($r.Status)"
    Write-Host "    $($r.Body)`n"
}

Write-Host '=== Rejects wrong content type ==='
$r = Invoke-Pipeline -Bytes ([byte[]](1..255)) -ContentType 'application/pdf' -FileName 'x.pdf'
Write-Host "HTTP $($r.Status) -> $($r.Body)`n"

Write-Host '=== Rejects oversize upload (3MB vs 2MB cap) ==='
$big = New-Object byte[] (3 * 1024 * 1024)
try {
    $r = Invoke-Pipeline -Bytes $big
    Write-Host "HTTP $($r.Status) -> $($r.Body)`n"
} catch {
    Write-Host "Connection closed by server (expected for oversize)`n"
}

Write-Host '=== Rate limit (20/hr cap) ==='
$hit = $false
for ($i = 1; $i -le 22; $i++) {
    $r = Invoke-Pipeline -Bytes $wav
    if ($r.Status -eq 429) {
        Write-Host "Limiter engaged at request $i -> $($r.Body)"
        $hit = $true
        break
    }
}
if (-not $hit) { Write-Host 'WARNING: limiter never engaged' }
