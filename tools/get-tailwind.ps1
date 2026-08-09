# Downloads the Tailwind standalone CLI used by the build.
# The binary is ~110MB and is intentionally not committed.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$target = Join-Path $PSScriptRoot 'tailwindcss.exe'

if (Test-Path -LiteralPath $target) {
    Write-Host "Tailwind CLI already present at $target"
    exit 0
}

$url = 'https://github.com/tailwindlabs/tailwindcss/releases/latest/download/tailwindcss-windows-x64.exe'

Write-Host "Downloading Tailwind CLI..."
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing

Write-Host "Saved to $target"
