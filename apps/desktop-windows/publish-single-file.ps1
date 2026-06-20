param(
    [string]$OutputPath = "$([Environment]::GetFolderPath('Desktop'))\IPMultiViewer.Desktop.exe"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "IPMultiViewer.Desktop.csproj"
$publishDir = Join-Path $PSScriptRoot "publish\single-file"

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publishDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false

$sourceExe = Join-Path $publishDir "IPMultiViewer.Desktop.exe"
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Publish did not produce expected executable: $sourceExe"
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

Copy-Item -LiteralPath $sourceExe -Destination $OutputPath -Force
Write-Host "Published single-file executable: $OutputPath"
