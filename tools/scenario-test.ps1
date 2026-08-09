# Exercises every pipeline branch by filename, using the stub transcriber.
# Filenames map to scenarios in StubTranscriptionService.Resolve.

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

    $bw.Write([char[]]'RIFF');  $bw.Write([int]($dataBytes + 36))
    $bw.Write([char[]]'WAVE')
    $bw.Write([char[]]'fmt ');  $bw.Write([int]16)
    $bw.Write([int16]1);        $bw.Write([int16]1)
    $bw.Write([int]$SampleRate); $bw.Write([int]($SampleRate * 2))
    $bw.Write([int16]2);        $bw.Write([int16]16)
    $bw.Write([char[]]'data');  $bw.Write([int]$dataBytes)

    for ($i = 0; $i -lt $samples; $i++) {
        $v = [int16](3000 * [Math]::Sin(2 * [Math]::PI * 440 * $i / $SampleRate))
        $bw.Write($v)
    }

    $bw.Flush()
    return $ms.ToArray()
}

function Invoke-Pipeline {
    param([byte[]]$Bytes, [string]$ContentType = 'audio/wav', [string]$FileName)

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $part = [System.Net.Http.ByteArrayContent]::new($Bytes)
    $part.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($ContentType)
    $form.Add($part, 'audio', $FileName)

    $resp = $client.PostAsync($url, $form).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $client.Dispose()

    return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $body }
}

$wav = New-WavBytes -Seconds 3

$cases = @(
    @{ File = '01-clean-adiba-500.wav'; Expect = 'confirm';           Flagged = $false },
    @{ File = '02-clean-tanvir-900.wav'; Expect = 'confirm';          Flagged = $false },
    @{ File = '03b-mumble-heavy.wav';   Expect = 'confirm';           Flagged = $true  },
    @{ File = '03c-noisy.wav';          Expect = 'confirm';           Flagged = $true  },
    @{ File = '04-balance.wav';         Expect = 'balance';           Flagged = $false },
    @{ File = '05-nonsense.wav';        Expect = 'unrecognized';      Flagged = $false },
    @{ File = '07-over-balance.wav';    Expect = 'over_balance';      Flagged = $false }
)

$pass = 0
$fail = 0

foreach ($c in $cases) {
    $r = Invoke-Pipeline -Bytes $wav -FileName $c.File

    if ($r.Status -ne 200) {
        Write-Host "FAIL $($c.File): HTTP $($r.Status)" -ForegroundColor Red
        Write-Host "     $($r.Body)"
        $fail++
        continue
    }

    $json = $r.Body | ConvertFrom-Json
    $okStatus = $json.status -eq $c.Expect
    $okFlag   = [bool]$json.needsConfirmation -eq [bool]$c.Flagged

    if ($okStatus -and $okFlag) {
        Write-Host "PASS $($c.File) -> $($json.status)" -NoNewline -ForegroundColor Green
        if ($json.flags.Count -gt 0) { Write-Host "  flags=[$($json.flags -join ', ')]" } else { Write-Host '' }
        if ($json.confidenceReason) { Write-Host "     reason: $($json.confidenceReason)" }
        $pass++
    } else {
        Write-Host "FAIL $($c.File)" -ForegroundColor Red
        Write-Host "     expected status=$($c.Expect) flagged=$($c.Flagged)"
        Write-Host "     actual   status=$($json.status) flagged=$($json.needsConfirmation) flags=[$($json.flags -join ', ')]"
        $fail++
    }
}

Write-Host "`n$pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
