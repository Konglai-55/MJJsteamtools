[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '2.7.1'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'SteamLuaManager.csproj'
$bootstrapperProject = Join-Path $projectRoot 'installer\Bootstrapper\Bootstrapper.csproj'
$publishDir = Join-Path $projectRoot 'artifacts\publish\win-x64'
$bootstrapPublishDir = Join-Path $projectRoot 'artifacts\obj\bootstrapper-publish'
$installerDir = Join-Path $projectRoot 'artifacts\installer'
$payloadZip = Join-Path $projectRoot 'artifacts\obj\MJJsteamtools-payload.zip'
$outputPath = Join-Path $installerDir "MJJsteamtools-Setup-$Version-x64.exe"

New-Item -ItemType Directory -Force -Path $publishDir, $bootstrapPublishDir, $installerDir, (Split-Path $payloadZip) | Out-Null
Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

& dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
    -p:SkipSvcMonitorBuild=true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Application publish failed with exit code $LASTEXITCODE" }

Remove-Item -LiteralPath $payloadZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $payloadZip -CompressionLevel Optimal

Remove-Item -LiteralPath $bootstrapPublishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $bootstrapPublishDir | Out-Null
& dotnet publish $bootstrapperProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -p:Version=$Version -o $bootstrapPublishDir
if ($LASTEXITCODE -ne 0) { throw "Bootstrapper publish failed with exit code $LASTEXITCODE" }

$stub = Join-Path $bootstrapPublishDir 'MJJsteamtools-Setup.exe'
if (-not (Test-Path -LiteralPath $stub)) { throw "Bootstrapper output not found: $stub" }
Copy-Item -LiteralPath $stub -Destination $outputPath -Force
$payloadBytes = [IO.File]::ReadAllBytes($payloadZip)
$stream = [IO.File]::Open($outputPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try {
    $stream.Write($payloadBytes, 0, $payloadBytes.Length)
    $lengthBytes = [BitConverter]::GetBytes([Int64]$payloadBytes.Length)
    $stream.Write($lengthBytes, 0, $lengthBytes.Length)
} finally {
    $stream.Dispose()
}

$output = Get-Item -LiteralPath $outputPath
$hash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash
Write-Host "EXE installer created: $($output.FullName)"
Write-Host ("Size: {0:N2} MB" -f ($output.Length / 1MB))
Write-Host "SHA256: $hash"

