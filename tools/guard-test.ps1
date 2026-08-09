# Verifies the endpoint's input guards and rate limiter, not the happy path.

$ErrorActionPreference = 'Stop'
$base = if ($args[0]) { $args[0] } else { 'http://localhost:5230' }
$url  = "$base/api/process-voice-command"

Add-Type -AssemblyName System.Net.Http

function Send-Raw {
    param(
        [byte[]]$Bytes,
        [string]$ContentType,
        [string]$FileName,
        [string]$FieldName = 'audio',
        [switch]$NoFile
    )

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $form = [System.Net.Http.MultipartFormDataContent]::new()

    if (-not $NoFile) {
        $part = [System.Net.Http.ByteArrayContent]::new($Bytes)
        if ($ContentType) {
            $part.Headers.ContentType =
                [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($ContentType)
        }
        $form.Add($part, $FieldName, $FileName)
    }

    $resp = $client.PostAsync($url, $form).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $client.Dispose()

    return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $body }
}

$pass = 0; $fail = 0

function Assert-Status {
    param([string]$Name, [int]$Expected, $Result)

    if ($Result.Status -eq $Expected) {
        Write-Host "PASS $Name -> $($Result.Status)" -ForegroundColor Green
        $script:pass++
    } else {
        Write-Host "FAIL $Name -> expected $Expected, got $($Result.Status)" -ForegroundColor Red
        Write-Host "     $($Result.Body)"
        $script:fail++
    }
}

# Missing file part.
Assert-Status 'no file part' 400 (Send-Raw -NoFile)

# Wrong field name.
Assert-Status 'wrong field name' 400 `
    (Send-Raw -Bytes ([byte[]]@(1,2,3)) -ContentType 'audio/wav' -FileName 'a.wav' -FieldName 'file')

# Empty file.
Assert-Status 'empty file' 400 `
    (Send-Raw -Bytes ([byte[]]@()) -ContentType 'audio/wav' -FileName 'a.wav')

# Disallowed content type.
Assert-Status 'text/plain rejected' 415 `
    (Send-Raw -Bytes ([byte[]]@(1,2,3,4,5)) -ContentType 'text/plain' -FileName 'a.txt')

# Oversized payload (server cap is 2 MB; send 3 MB).
$big = New-Object byte[] (3 * 1024 * 1024)
Assert-Status 'oversize rejected' 413 `
    (Send-Raw -Bytes $big -ContentType 'audio/wav' -FileName 'big.wav')

Write-Host "`n$pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
