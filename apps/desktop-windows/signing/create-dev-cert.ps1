param(
    [string]$Subject = "CN=Jumala9163, Kome-Lab",
    [string]$OutputPath = "$PSScriptRoot\ip-multiviewer-dev-codesign.pfx",
    [Parameter(Mandatory = $true)]
    [securestring]$Password
)

$cert = New-SelfSignedCertificate `
    -Subject $Subject `
    -Type CodeSigningCert `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddYears(3)

Export-PfxCertificate -Cert $cert -FilePath $OutputPath -Password $Password | Out-Null
Write-Host "Created development code signing certificate:"
Write-Host "  Subject: $Subject"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  PFX: $OutputPath"
Write-Host "Do not commit the PFX file or password."
