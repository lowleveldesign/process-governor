$ErrorActionPreference="Stop"

Invoke-WebRequest -Uri "https://aka.ms/wingetcreate/latest" -OutFile "wingetcreate.exe"

$BuildNumber = $env:GITHUB_RUN_NUMBER % [int16]::MaxValue
$Tag = [System.IO.Path]::GetFileName("$env:GITHUB_REF")
if (-not ($Tag -match "^(\d+\.\d+\.\d+)(\-.+)?$")) { Write-Error "Invalid tag name for the release." }
$Version = "$($Matches[1]).$buildNumber"

$FileHash = $(Get-FileHash -Algorithm SHA256 "procgov.zip").Hash
Get-ChildItem -Path ".winget" -Filter *.yaml | Foreach-Object -Process {
    $FilePath = $_.FullName
    $Content = Get-Content -Encoding UTF8 $FilePath
    $Content = $Content -replace "###VERSION###", $Version -replace "###FILE_HASH###", $FileHash -replace "###TAG###", $Tag
    Set-Content -Encoding Utf8 -Path $FilePath -Value $Content
}

.\wingetcreate.exe submit -p "procgov v$Version" .winget
