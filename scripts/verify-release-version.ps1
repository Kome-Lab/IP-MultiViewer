$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolver = Join-Path $PSScriptRoot 'resolve-release-version.ps1'

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Actual,
        [Parameter(Mandatory = $true)]
        [object]$Expected,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not [object]::Equals($Actual, $Expected)) {
        throw "$Label was '$Actual'; expected '$Expected'."
    }
}

$stable = & $resolver -GitTag 'v2.3.4'
Assert-Equal $stable.GitTag 'v2.3.4' 'stable Git tag'
Assert-Equal $stable.SoftwareVersion '2.3.4' 'stable software version'
Assert-Equal $stable.BaseVersion '2.3.4' 'stable base version'
Assert-Equal $stable.FileVersion '2.3.4.0' 'stable file version'
Assert-Equal $stable.IsPrerelease $false 'stable prerelease flag'

$prerelease = & $resolver -GitTag 'v2.3.4-rc.2'
Assert-Equal $prerelease.SoftwareVersion '2.3.4-rc.2' 'prerelease software version'
Assert-Equal $prerelease.BaseVersion '2.3.4' 'prerelease base version'
Assert-Equal $prerelease.FileVersion '2.3.4.0' 'prerelease file version'
Assert-Equal $prerelease.IsPrerelease $true 'prerelease flag'

$invalidRejected = $false
try {
    $null = & $resolver -GitTag 'v2.3.4.5'
}
catch {
    $invalidRejected = $true
}
if (-not $invalidRejected) {
    throw 'A four-component Git tag was accepted unexpectedly.'
}

Write-Host 'release_version_mapping_ok'
