param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,
    [string]$Subject = "Jumala9163, Kome-Lab",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$SignToolPath = "signtool.exe",
    [string]$Thumbprint = ""
)

if (-not (Test-Path -LiteralPath $FilePath)) {
    throw "File not found: $FilePath"
}

$args = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256")
if ($Thumbprint) {
    $args += @("/sha1", $Thumbprint)
} else {
    $args += @("/n", $Subject)
}
$args += $FilePath

& $SignToolPath @args
if ($LASTEXITCODE -ne 0) {
    throw "signtool failed with exit code $LASTEXITCODE"
}
