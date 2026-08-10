# Runs every sample clip through the live pipeline and reports the real
# confidence metrics. This is the calibration step: the thresholds in
# appsettings.json are documented starting points, not measurements, and this
# is what replaces them with observed values.
#
# Requires the app running in Development with a real Groq key.

$ErrorActionPreference = 'Stop'
$base = if ($args[0]) { $args[0] } else { 'http://127.0.0.1:5099' }
$url = "$base/api/process-voice-command"
$dir = "G:\CV\BL\src\BoloPay.Web\wwwroot\sample-audio"

Add-Type -AssemblyName System.Net.Http

$expected = [ordered]@{
    '01-clean-adiba-500.wav'   = @{ Status = 'confirm';          Amount = 500;   Flag = $false }
    '02-clean-tanvir-900.wav'  = @{ Status = 'confirm';          Amount = 900;   Flag = $false }
    '03a-mumble-mild.wav'      = @{ Status = 'confirm';          Amount = 500;   Flag = $null  }
    '03b-mumble-heavy.wav'     = @{ Status = 'confirm';          Amount = 500;   Flag = $true  }
    '03c-noisy.wav'            = @{ Status = 'confirm';          Amount = 500;   Flag = $null  }
    '03d-stutter.wav'          = @{ Status = 'confirm';          Amount = 500;   Flag = $null  }
    '04-balance.wav'           = @{ Status = 'balance';          Amount = $null; Flag = $false }
    '05-nonsense.wav'          = @{ Status = 'unrecognized';     Amount = $null; Flag = $false }
    '06-unknown-recipient.wav' = @{ Status = 'unknown_recipient';Amount = 300;   Flag = $null  }
    '07-over-balance.wav'      = @{ Status = 'over_balance';     Amount = 50000; Flag = $null  }
}

$rows = @()

foreach ($file in $expected.Keys) {
    $path = Join-Path $dir $file
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "SKIP  $file (not found)" -ForegroundColor DarkGray
        continue
    }

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(120)
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $part = [System.Net.Http.ByteArrayContent]::new($bytes)
    $part.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('audio/wav')
    $form.Add($part, 'audio', $file)

    try {
        $resp = $client.PostAsync($url, $form).GetAwaiter().GetResult()
        $body = [System.Text.Encoding]::UTF8.GetString(
            $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())
    } finally {
        $client.Dispose()
    }

    if ([int]$resp.StatusCode -ne 200) {
        Write-Host "ERROR $file -> HTTP $([int]$resp.StatusCode)" -ForegroundColor Red
        Write-Host "      $body"
        continue
    }

    $j = $body | ConvertFrom-Json
    $d = $j.diagnostics

    $exp = $expected[$file]
    $okStatus = $j.status -eq $exp.Status
    $okAmount = ($null -eq $exp.Amount) -or ([string]$j.amountBdt -eq [string]$exp.Amount)
    $okFlag = ($null -eq $exp.Flag) -or ([bool]$j.needsConfirmation -eq $exp.Flag)

    $verdict = if ($okStatus -and $okAmount -and $okFlag) { 'PASS' } else { 'CHECK' }
    $colour = if ($verdict -eq 'PASS') { 'Green' } else { 'Yellow' }

    Write-Host "$verdict $file" -ForegroundColor $colour
    Write-Host "      status=$($j.status) amount=$($j.amountBdt) flagged=$($j.needsConfirmation) flags=[$($j.flags -join ',')]"
    Write-Host "      primary : `"$($d.primaryText)`""
    if ($d.crossText -and $d.crossText -ne $d.primaryText) {
        Write-Host "      cross   : `"$($d.crossText)`"" -ForegroundColor DarkCyan
    }
    Write-Host ("      logprob={0:F4}  nospeech={1:F4}  compression={2:F4}  segments={3}" -f `
        $d.worstAvgLogprob, $d.worstNoSpeechProb, $d.worstCompressionRatio, $d.segmentCount)
    Write-Host ""

    $rows += [pscustomobject]@{
        File            = $file
        Status          = $j.status
        Amount          = $j.amountBdt
        Flagged         = $j.needsConfirmation
        Flags           = ($j.flags -join ',')
        AvgLogprob      = [math]::Round([double]$d.worstAvgLogprob, 4)
        NoSpeechProb    = [math]::Round([double]$d.worstNoSpeechProb, 4)
        CompressionRatio= [math]::Round([double]$d.worstCompressionRatio, 4)
        Segments        = $d.segmentCount
        PrimaryText     = $d.primaryText
        CrossText       = $d.crossText
        Verdict         = $verdict
    }

    # Free tier is ~8000 TPM on the extraction model; pace to stay under it.
    Start-Sleep -Seconds 8
}

$csv = "G:\CV\BL\recordings\calibration-results.csv"
$rows | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
Write-Host "Wrote $csv" -ForegroundColor Cyan

Write-Host "`n=== avg_logprob separation ===" -ForegroundColor Cyan
$clean = $rows | Where-Object { $_.File -like '0[12]-clean*' }
$degraded = $rows | Where-Object { $_.File -like '03*' }

if ($clean -and $degraded) {
    $cleanWorst = ($clean | Measure-Object AvgLogprob -Minimum).Minimum
    $degradedBest = ($degraded | Measure-Object AvgLogprob -Maximum).Maximum
    Write-Host ("clean    worst : {0:F4}" -f $cleanWorst)
    Write-Host ("degraded best  : {0:F4}" -f $degradedBest)
    if ($cleanWorst -gt $degradedBest) {
        Write-Host ("SEPARATED - a threshold between {0:F4} and {1:F4} splits them" -f $degradedBest, $cleanWorst) -ForegroundColor Green
    } else {
        Write-Host "OVERLAP - avg_logprob does not separate clean from degraded here." -ForegroundColor Yellow
        Write-Host "This is a finding, not a bug: it is why cross-pass agreement exists." -ForegroundColor Yellow
    }
}
