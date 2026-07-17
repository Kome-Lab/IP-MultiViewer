param(
    [Parameter(Mandatory = $true)]
    [Alias('Version')]
    [string]$GitTag,

    [ValidateSet('standard', 'gpu')]
    [string[]]$Flavor = @('standard', 'gpu'),

    [string]$OutputDir = 'artifacts/windows-release',

    [switch]$AllowUnverifiedGitTag
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    [System.IO.Path]::GetFullPath($OutputDir)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDir))
}
$workParent = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/.windows-package-work'))
$workRoot = [System.IO.Path]::GetFullPath((Join-Path $workParent ("run-{0}-{1}" -f $PID, [System.Guid]::NewGuid().ToString('N'))))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$resolverArguments = @{ GitTag = $GitTag }
if (-not $AllowUnverifiedGitTag) {
    $resolverArguments.VerifyAtHead = $true
}
$releaseVersion = & (Join-Path $PSScriptRoot 'resolve-release-version.ps1') @resolverArguments
$softwareVersion = $releaseVersion.SoftwareVersion
$fileVersion = $releaseVersion.FileVersion

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Parent
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside ${resolvedParent}: $resolvedPath"
    }
}

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

function Publish-Desktop {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Project,
        [Parameter(Mandatory = $true)]
        [string]$Destination,
        [Parameter(Mandatory = $true)]
        [bool]$EnableGpuRendering
    )

    $arguments = @(
        'publish', $Project,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $Destination,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:IncludeAllContentForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        "-p:EnableGpuRendering=$($EnableGpuRendering.ToString().ToLowerInvariant())",
        "-p:Version=$softwareVersion",
        "-p:AssemblyVersion=$fileVersion",
        "-p:FileVersion=$fileVersion",
        "-p:InformationalVersion=$softwareVersion",
        '-p:IncludeSourceRevisionInInformationalVersion=false'
    )
    Invoke-Checked dotnet @arguments
}

Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force -Path $outputRoot, $workRoot | Out-Null

    foreach ($releaseFlavor in $Flavor) {
        $enableGpuRendering = $releaseFlavor -eq 'gpu'
        $packageSuffix = if ($enableGpuRendering) { '-gpu' } else { '' }
        $packageName = "IPMultiViewer-$GitTag-windows-x64$packageSuffix"
        $packageWork = Join-Path $workRoot $packageName
        $publishCurrent = Join-Path $packageWork 'publish/current'
        $staging = Join-Path $packageWork 'staging'
        $zipPath = Join-Path $outputRoot "$packageName.zip"
        $hashPath = "$zipPath.sha256"

        Assert-ChildPath -Path $packageWork -Parent $artifactsRoot
        if (Test-Path -LiteralPath $packageWork) {
            Remove-Item -LiteralPath $packageWork -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $publishCurrent, $staging | Out-Null
        Publish-Desktop `
            -Project 'apps/desktop-windows/IPMultiViewer.csproj' `
            -Destination $publishCurrent `
            -EnableGpuRendering $enableGpuRendering

        $currentSource = Join-Path $publishCurrent 'IPMultiViewer.exe'
        $currentDestination = Join-Path $staging 'IPMultiViewer.exe'
        $readmeDestination = Join-Path $staging 'README-WINDOWS.md'
        $versionDestination = Join-Path $staging 'VERSION.txt'

        Copy-Item -LiteralPath $currentSource -Destination $currentDestination -Force

        $flavorLabel = if ($enableGpuRendering) { 'GPU-enabled' } else { 'Standard' }
        @(
            '# IP MultiViewer for Windows'
            ''
            'Extract the ZIP to a writable folder, then launch `IPMultiViewer.exe`.'
            'Settings and logs are stored under `%ProgramData%\IPMultiViewer`.'
            ''
            "Package flavor: $flavorLabel."
            'For GPU builds, use the in-app `DECODE / OUTPUT` status as runtime evidence.'
            ''
            '## Verify the download'
            'Download the matching `.zip.sha256` sidecar from the release page.'
            'Run `Get-FileHash <zip-path> -Algorithm SHA256` and compare the result before extraction.'
            ''
            '## Offline update'
            '1. Close IP MultiViewer.'
            '2. Keep the current application folder and its release ZIP as the rollback copy.'
            '3. Extract the new ZIP into a new folder; do not overwrite the running folder.'
            '4. Launch the new `IPMultiViewer.exe` and confirm playback, meters, and alerts.'
            '5. If rollback is required, close it and launch `IPMultiViewer.exe` from the previous version folder.'
        ) | Set-Content -Encoding utf8 -Path $readmeDestination

        @(
            'IP MultiViewer for Windows'
            "Git tag: $GitTag"
            "Software version: $softwareVersion"
            "File version: $fileVersion"
            'Runtime: windows-x64'
            "Flavor: $flavorLabel"
            'Primary executable: IPMultiViewer.exe'
        ) | Set-Content -Encoding utf8 -Path $versionDestination

        $requiredFiles = @(
            $currentDestination,
            $readmeDestination,
            $versionDestination
        )
        foreach ($requiredFile in $requiredFiles) {
            if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
                throw "Release staging is missing: $requiredFile"
            }
        }

        $versionInfo = (Get-Item -LiteralPath $currentDestination).VersionInfo
        if ($versionInfo.ProductName -ne 'IP MultiViewer') {
            throw "Unexpected ProductName in release executable: $($versionInfo.ProductName)"
        }
        if ($versionInfo.FileDescription -ne 'IP MultiViewer') {
            throw "Unexpected FileDescription in release executable: $($versionInfo.FileDescription)"
        }
        if (-not [string]::Equals($versionInfo.ProductVersion, $softwareVersion, [System.StringComparison]::Ordinal)) {
            throw "Unexpected ProductVersion in release executable: $($versionInfo.ProductVersion)"
        }
        if (-not [string]::Equals($versionInfo.FileVersion, $fileVersion, [System.StringComparison]::Ordinal)) {
            throw "Unexpected FileVersion in release executable: $($versionInfo.FileVersion)"
        }
        $identityText = "$($versionInfo.InternalName) $($versionInfo.OriginalFilename) $($versionInfo.ProductName) $($versionInfo.FileDescription)"
        if ($identityText -match '(?i)next|legacy') {
            throw "Pre-release identity leaked into release executable metadata: $identityText"
        }

        foreach ($outputFile in @($zipPath, $hashPath)) {
            if (Test-Path -LiteralPath $outputFile) {
                Remove-Item -LiteralPath $outputFile -Force
            }
        }

        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $entries = @(
                $archive.Entries |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) } |
                    ForEach-Object { $_.FullName.Replace('\', '/') } |
                    Sort-Object
            )
        }
        finally {
            $archive.Dispose()
        }
        $expectedEntries = @('IPMultiViewer.exe', 'README-WINDOWS.md', 'VERSION.txt') | Sort-Object
        if (($entries -join "`n") -ne ($expectedEntries -join "`n")) {
            throw "Unexpected release archive contents: $($entries -join ', ')"
        }

        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
        "$($hash.Hash.ToLowerInvariant())  $packageName.zip" | Set-Content -NoNewline -Encoding ascii -Path $hashPath

        Write-Host "windows_release_package=$zipPath"
        Write-Host "windows_release_sha256=$($hash.Hash.ToLowerInvariant())"

        Remove-Item -LiteralPath $packageWork -Recurse -Force
    }
}
finally {
    try {
        Assert-ChildPath -Path $workRoot -Parent $artifactsRoot
        if (Test-Path -LiteralPath $workRoot) {
            Remove-Item -LiteralPath $workRoot -Recurse -Force
        }
    }
    finally {
        Pop-Location
    }
}
