param(
    [string]$Version = "dev",
    [string[]]$Runtime = @("linux-x64", "linux-arm64"),
    [string]$OutputDir = "artifacts/server"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir
} else {
    Join-Path $repoRoot $OutputDir
}
$distRoot = Join-Path $repoRoot "dist/server-packages"

$runtimeMap = @{
    "linux-x64" = @{
        GOOS = "linux"
        GOARCH = "amd64"
        Extension = ""
    }
    "linux-arm64" = @{
        GOOS = "linux"
        GOARCH = "arm64"
        Extension = ""
    }
    "windows-x64" = @{
        GOOS = "windows"
        GOARCH = "amd64"
        Extension = ".exe"
    }
}

$Runtime = @(
    foreach ($item in $Runtime) {
        foreach ($part in ($item -split ",")) {
            $normalized = $part.Trim()
            if ($normalized) {
                $normalized
            }
        }
    }
)

foreach ($targetRuntime in $Runtime) {
    if (-not $runtimeMap.ContainsKey($targetRuntime)) {
        throw "Unsupported runtime '$targetRuntime'. Supported runtimes: $($runtimeMap.Keys -join ', ')"
    }
}

function Write-PackageReadme {
    param(
        [string]$Path,
        [string]$PackageName,
        [string]$PackageVersion,
        [string]$PackageRuntime
    )

    $content = @"
# IP MultiViewer Server package

Package: $PackageName
Version: $PackageVersion
Runtime: $PackageRuntime

## Contents

- ip-multiviewer-server
- ip-multiviewer-agent
- systemd service examples
- deploy/server.env.example
- deploy/agent.env.example
- .env.example
- docker-compose.prod.yml
- migrations/

## Minimal systemd install

1. Create the service user and directories.
2. Install the server and agent binaries to /usr/local/bin.
3. Copy deploy/*.env.example to /etc/ip-multiviewer/*.env and replace placeholders.
4. Copy systemd/*.service.example to /etc/systemd/system/*.service.
5. Run systemctl daemon-reload, then enable and start the services.

See ip-multiviewer-docs/docs/installation/server-and-agent.md for the full procedure.
"@

    Set-Content -Encoding UTF8 -Path $Path -Value $content
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outputRoot, $distRoot | Out-Null

$previousGoos = $env:GOOS
$previousGoarch = $env:GOARCH
$previousCgo = $env:CGO_ENABLED

Push-Location $repoRoot
try {
    foreach ($targetRuntime in $Runtime) {
        $target = $runtimeMap[$targetRuntime]
        $runtimeName = $targetRuntime.Replace("-", "_")
        $packageName = "ip-multiviewer_${Version}_server_${runtimeName}"
        $staging = Join-Path $distRoot $packageName
        $serverOut = Join-Path $staging ("ip-multiviewer-server" + $target.Extension)
        $agentOut = Join-Path $staging ("ip-multiviewer-agent" + $target.Extension)

        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $staging | Out-Null

        $env:GOOS = $target.GOOS
        $env:GOARCH = $target.GOARCH
        $env:CGO_ENABLED = "0"

        go build -trimpath -ldflags "-s -w" -o $serverOut ./apps/server/cmd/server
        go build -trimpath -ldflags "-s -w" -o $agentOut ./apps/agent/cmd/agent

        Copy-Item -LiteralPath (Join-Path $repoRoot "systemd") -Destination (Join-Path $staging "systemd") -Recurse
        Copy-Item -LiteralPath (Join-Path $repoRoot "deploy") -Destination (Join-Path $staging "deploy") -Recurse
        Copy-Item -LiteralPath (Join-Path $repoRoot "migrations") -Destination (Join-Path $staging "migrations") -Recurse
        Copy-Item -LiteralPath (Join-Path $repoRoot ".env.example") -Destination $staging
        Copy-Item -LiteralPath (Join-Path $repoRoot "docker-compose.prod.yml") -Destination $staging
        Write-PackageReadme -Path (Join-Path $staging "README-server-package.md") -PackageName $packageName -PackageVersion $Version -PackageRuntime $targetRuntime

        $zipPath = Join-Path $outputRoot "$packageName.zip"
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }
        Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath

        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
        "$($hash.Hash.ToLowerInvariant())  $packageName.zip" | Set-Content -NoNewline -Encoding ascii -Path "$zipPath.sha256"
        Write-Host "Packaged $zipPath"
    }
}
finally {
    Pop-Location
    $env:GOOS = $previousGoos
    $env:GOARCH = $previousGoarch
    $env:CGO_ENABLED = $previousCgo
}
