param(
    [Parameter(Mandatory = $true)]
    [string]$GitTag,

    [switch]$VerifyAtHead
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tagPattern = '^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$'
if ($GitTag -notmatch $tagPattern) {
    throw "Git tag must look like v1.2.3 or v1.2.3-rc.1; got $GitTag"
}

$softwareVersion = $GitTag.Substring(1)
$baseVersion = $softwareVersion.Split('-')[0]
$fileVersion = "$baseVersion.0"

if ($VerifyAtHead) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    Push-Location $repoRoot
    try {
        $tagCommit = [string]::Join('', @(& git rev-list -n 1 $GitTag 2>$null)).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tagCommit)) {
            throw "Git tag does not exist: $GitTag"
        }

        $headCommit = [string]::Join('', @(& git rev-parse HEAD 2>$null)).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($headCommit)) {
            throw 'Could not resolve the current Git commit.'
        }

        if (-not $tagCommit.Equals($headCommit, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Git tag $GitTag points to $tagCommit, but HEAD is $headCommit."
        }

        $worktreeStatus = @(& git status --porcelain --untracked-files=all)
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not inspect the Git working tree.'
        }
        if ($worktreeStatus.Count -gt 0) {
            throw "Git working tree must be clean before packaging tag $GitTag."
        }
    }
    finally {
        Pop-Location
    }
}

[pscustomobject]@{
    GitTag = $GitTag
    SoftwareVersion = $softwareVersion
    BaseVersion = $baseVersion
    FileVersion = $fileVersion
    IsPrerelease = $softwareVersion.Contains('-')
}
