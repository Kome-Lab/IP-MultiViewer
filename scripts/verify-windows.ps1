param(
    [switch]$PublishDryRun,
    [switch]$SkipLiveMeterQa,
    [switch]$SkipMediaIntegrationQa
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$qaOutput = Join-Path $repoRoot "artifacts\windows-qa\ip-multiviewer-ui-vu.png"
$previousSkipLiveMeterQa = $env:IPMV_SKIP_LIVE_METER_QA
$previousSkipMediaIntegrationQa = $env:IPMV_SKIP_MEDIA_INTEGRATION_QA
$publishDryRunRoot = $null

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

Push-Location $repoRoot
try {
    if ($SkipLiveMeterQa -or $SkipMediaIntegrationQa) {
        $env:IPMV_SKIP_LIVE_METER_QA = "1"
    } else {
        $env:IPMV_SKIP_LIVE_METER_QA = $null
    }

    if ($SkipMediaIntegrationQa) {
        $env:IPMV_SKIP_MEDIA_INTEGRATION_QA = "1"
    } else {
        $env:IPMV_SKIP_MEDIA_INTEGRATION_QA = $null
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $qaOutput) | Out-Null

    Write-Host "[verify] Git tag to software version mapping"
    & (Join-Path $repoRoot "scripts\verify-release-version.ps1")

    Write-Host "[verify] Go packages"
    Invoke-Checked go test ./...
    Invoke-Checked go build ./...

    Write-Host "[verify] Current Windows desktop build"
    Invoke-Checked dotnet build apps/desktop-windows/IPMultiViewer.csproj --configuration Debug

    Write-Host "[verify] Extracted desktop boundaries"
    Invoke-Checked dotnet run --project packages/desktop-core/self-test/IPMultiViewer.Core.SelfTest.csproj --configuration Release

    Write-Host "[verify] Current Windows runtime smoke"
    Invoke-Checked dotnet run --project apps/desktop-windows/runtime-smoke/IPMultiViewer.RuntimeSmoke.csproj --configuration Debug

    if ($SkipLiveMeterQa -or $SkipMediaIntegrationQa) {
        Write-Host "[verify] Current offline UI and design smoke (live media fixture skipped)"
    } else {
        Write-Host "[verify] Current live meter and design smoke"
    }
    Invoke-Checked dotnet run --project apps/desktop-windows/qa/IPMultiViewer.QA.csproj --configuration Debug -- $qaOutput

    if ($PublishDryRun) {
        Write-Host "[verify] Current release publish dry run"
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $publishDryRunRoot = [System.IO.Path]::GetFullPath((Join-Path $tempRoot ("IPMultiViewer\verify-publish-{0}-{1}" -f $PID, [System.Guid]::NewGuid().ToString('N'))))
        if (-not $publishDryRunRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to use a publish dry-run path outside the temporary directory: $publishDryRunRoot"
        }

        Invoke-Checked dotnet publish apps/desktop-windows/IPMultiViewer.csproj --configuration Release --runtime win-x64 --self-contained true --output $publishDryRunRoot

        $currentExe = Join-Path $publishDryRunRoot "IPMultiViewer.exe"
        if (-not (Test-Path -LiteralPath $currentExe -PathType Leaf)) {
            throw "Publish dry run did not produce the current IPMultiViewer executable."
        }
    }

    Write-Host "windows_verification_ok qa=$qaOutput"
}
finally {
    try {
        if ($null -ne $publishDryRunRoot -and (Test-Path -LiteralPath $publishDryRunRoot)) {
            $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
            $resolvedPublishDryRunRoot = [System.IO.Path]::GetFullPath($publishDryRunRoot)
            if (-not $resolvedPublishDryRunRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to clean a publish dry-run path outside the temporary directory: $resolvedPublishDryRunRoot"
            }
            Remove-Item -LiteralPath $resolvedPublishDryRunRoot -Recurse -Force
        }
    }
    finally {
        $env:IPMV_SKIP_LIVE_METER_QA = $previousSkipLiveMeterQa
        $env:IPMV_SKIP_MEDIA_INTEGRATION_QA = $previousSkipMediaIntegrationQa
        Pop-Location
    }
}
