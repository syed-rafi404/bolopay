# Measures how reliably the recipient name survives the pipeline.
#
# Runs each name-bearing clip several times and reports what was heard, what
# it matched to, and how often the name was lost entirely. Recipient errors
# matter more than amount errors here: sending the right amount to the wrong
# person is the worse failure.

$ErrorActionPreference = 'Stop'
$base = if ($args[0]) { $args[0] } else { 'http://127.0.0.1:5052' }
$reps = if ($args[1]) { [int]$args[1] } else { 4 }
$dir  = 'G:\CV\BL\src\BoloPay.Web\wwwroot\sample-audio'

$clips = @(
    @{ File = '01-clean-adiba-500.wav';   Expect = 'Adiba'  }
    @{ File = '02-clean-tanvir-900.wav';  Expect = 'Tanvir' }
    @{ File = '03b-mumble-heavy.wav';     Expect = 'Adiba'  }
    @{ File = '07-over-balance.wav';      Expect = 'Amma'   }
    @{ File = '06-unknown-recipient.wav'; Expect = ''       }  # deliberately unknown
)

$rows = @()

foreach ($c in $clips) {
    $path = Join-Path $dir $c.File
    if (-not (Test-Path -LiteralPath $path)) { continue }

    Write-Host "=== $($c.File)  (expect: $(if ($c.Expect) { $c.Expect } else { 'no match' })) ===" -ForegroundColor Cyan

    for ($i = 1; $i -le $reps; $i++) {
        $raw = & curl.exe -s --max-time 90 -X POST "$base/api/process-voice-command" -F "audio=@$path;type=audio/wav"
        $r = [System.Text.Encoding]::UTF8.GetString([System.Text.Encoding]::UTF8.GetBytes($raw)) | ConvertFrom-Json

        $heard   = $r.recipientHeard
        $matched = $r.recipientName
        $score   = $r.diagnostics.contactMatchScore

        $ok = if ($c.Expect) { $matched -eq $c.Expect } else { [string]::IsNullOrEmpty($matched) }
        $colour = if ($ok) { 'Green' } else { 'Red' }

        Write-Host ("  run {0}: status={1,-18} heard='{2}' -> matched='{3}' score={4}" -f `
            $i, $r.status, $heard, $matched, $score) -ForegroundColor $colour
        Write-Host ("         transcript: {0}" -f $r.transcript) -ForegroundColor DarkGray

        $rows += [pscustomobject]@{
            File       = $c.File
            Expect     = $c.Expect
            Status     = $r.status
            Heard      = $heard
            Matched    = $matched
            Score      = $score
            Transcript = $r.transcript
            Ok         = $ok
        }

        Start-Sleep -Seconds 7
    }
    Write-Host ''
}

Write-Host '=== SUMMARY ===' -ForegroundColor Cyan
$rows | Group-Object File | ForEach-Object {
    $tot = $_.Count
    $good = @($_.Group | Where-Object Ok).Count
    $lost = @($_.Group | Where-Object { [string]::IsNullOrEmpty($_.Heard) }).Count
    "{0,-28} correct {1}/{2}   name-not-heard {3}/{2}" -f $_.Name, $good, $tot, $lost
}

$csv = 'G:\CV\BL\recordings\recipient-reliability.csv'
$rows | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
Write-Host "`nWrote $csv"
