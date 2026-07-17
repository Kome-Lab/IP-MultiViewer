[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$KeepReleaseArtifacts,
    [switch]$KeepQaEvidence,
    [switch]$IncludeRuntimeCache
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoPrefix = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$targets = New-Object System.Collections.Generic.List[string]
$commandContext = $PSCmdlet

function Add-CleanupTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the repository: $resolvedPath"
    }

    if ((Test-Path -LiteralPath $resolvedPath) -and -not $targets.Contains($resolvedPath)) {
        $targets.Add($resolvedPath)
    }
}

foreach ($relativeRoot in @('apps', 'packages')) {
    $scanRoot = Join-Path $repoRoot $relativeRoot
    if (-not (Test-Path -LiteralPath $scanRoot -PathType Container)) {
        continue
    }

    Get-ChildItem -LiteralPath $scanRoot -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -in @('bin', 'obj', 'publish', 'TestResults') -and
            -not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint)
        } |
        ForEach-Object { Add-CleanupTarget -Path $_.FullName }
}

foreach ($relativePath in @('dist', '.local-runtime', '.vs', 'TestResults')) {
    Add-CleanupTarget -Path (Join-Path $repoRoot $relativePath)
}

if (Test-Path -LiteralPath $artifactsRoot -PathType Container) {
    $preservedArtifactNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    if ($KeepReleaseArtifacts) {
        [void]$preservedArtifactNames.Add('release')
        [void]$preservedArtifactNames.Add('server')
        [void]$preservedArtifactNames.Add('windows-release')
    }
    if ($KeepQaEvidence) {
        [void]$preservedArtifactNames.Add('windows-qa')
    }

    foreach ($artifactItem in @(Get-ChildItem -LiteralPath $artifactsRoot -Force)) {
        if ($preservedArtifactNames.Contains($artifactItem.Name)) {
            continue
        }

        Add-CleanupTarget -Path $artifactItem.FullName
    }
}

$orderedTargets = @($targets | Sort-Object { $_.Length } -Descending)
foreach ($target in $orderedTargets) {
    if (-not (Test-Path -LiteralPath $target)) {
        continue
    }

    if ($commandContext.ShouldProcess($target, 'Remove generated output')) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "removed_generated=$target"
    }
}

if ($IncludeRuntimeCache) {
    $runningApplication = @(
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -match '^IPMultiViewer(?:\.Next)?$' }
    )
    if ($runningApplication.Count -gt 0) {
        throw 'Close IP MultiViewer before cleaning its runtime cache.'
    }

    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $tempPrefix = $tempRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $runtimeTargets = New-Object System.Collections.Generic.List[string]
    $dotnetBundleRoot = Join-Path $tempRoot '.net'
    if (Test-Path -LiteralPath $dotnetBundleRoot -PathType Container) {
        Get-ChildItem -LiteralPath $dotnetBundleRoot -Force |
            Where-Object { $_.Name -match '^IPMultiViewer(?:\.Next)?$' } |
            ForEach-Object { $runtimeTargets.Add($_.FullName) }
    }

    Get-ChildItem -LiteralPath $tempRoot -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -match '^ipmv-desktop-smoke-' -or
            $_.Name -match '^ipmv-runtime-smoke-' -or
            $_.Name -match '^ipmv-next-' -or
            $_.Name -match '^ipmv-windows-' -or
            $_.Name -match '^ipmv-gpu-evidence-'
        } |
        ForEach-Object { $runtimeTargets.Add($_.FullName) }

    foreach ($runtimeTarget in @($runtimeTargets)) {
        $resolvedRuntimeTarget = [System.IO.Path]::GetFullPath($runtimeTarget)
        if (-not $resolvedRuntimeTarget.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a runtime path outside the temporary directory: $resolvedRuntimeTarget"
        }

        if ($commandContext.ShouldProcess($resolvedRuntimeTarget, 'Remove IP MultiViewer runtime cache')) {
            Remove-Item -LiteralPath $resolvedRuntimeTarget -Recurse -Force
            Write-Host "removed_runtime_cache=$resolvedRuntimeTarget"
        }
    }
}

Write-Host "generated_cleanup_ok removed=$($orderedTargets.Count)"
