[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '2.7.1'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'SteamLuaManager.csproj'
$publishDir = Join-Path $projectRoot 'artifacts\publish\win-x64'
$installerDir = Join-Path $projectRoot 'artifacts\installer'
$intermediateDir = Join-Path $projectRoot 'artifacts\obj\wix'
$wixExe = Join-Path $projectRoot '.tools\wix.exe'
$wixSource = Join-Path $projectRoot 'installer\Package.wxs'
$outputPath = Join-Path $installerDir "MJJsteamtools-Setup-$Version-x64.msi"

if (-not (Test-Path -LiteralPath $wixExe)) {
    throw 'WiX 5 was not found. Run: dotnet tool install wix --tool-path .tools --version 5.0.2'
}

New-Item -ItemType Directory -Force -Path $publishDir, $installerDir, $intermediateDir | Out-Null

& dotnet publish $projectFile `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:SkipSvcMonitorBuild=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "Application publish failed with exit code $LASTEXITCODE"
}

& $wixExe build $wixSource `
    -arch x64 `
    -d "Version=$Version" `
    -d "ProjectDir=$projectRoot" `
    -d "PublishDir=$publishDir" `
    -dcl high `
    -intermediatefolder $intermediateDir `
    -pdbtype none `
    -out $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "Installer build failed with exit code $LASTEXITCODE"
}

$output = Get-Item -LiteralPath $outputPath
Write-Host "Installer created: $($output.FullName)"
Write-Host ("Size: {0:N2} MB" -f ($output.Length / 1MB))

$exeBuilder = Join-Path $projectRoot 'build-exe-installer.ps1'
if (Test-Path -LiteralPath $exeBuilder) {
    & $exeBuilder -Version $Version
    if ($LASTEXITCODE -ne 0) {
        throw "EXE installer build failed with exit code $LASTEXITCODE"
    }
}

