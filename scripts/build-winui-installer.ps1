param(
    [string]$Version = '1.1.0',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$Runtime = 'win-x64',
    [string]$Publisher = 'CN=HealthChecker'
)

$ErrorActionPreference = 'Stop'

function Convert-ToAppxVersion {
    param([string]$RawVersion)

    $normalized = $RawVersion.Trim()
    if ($normalized.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    if (-not ($normalized -match '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$')) {
        throw "Version '$RawVersion' is invalid. Use semver format like 1.1.0 or 1.1.0.0"
    }

    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3]
    $rev = if ($Matches[4]) { [int]$Matches[4] } else { 0 }

    return "$major.$minor.$patch.$rev"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$projectPath = Join-Path $repoRoot 'HealthChecker.WinUI\HealthChecker.WinUI.csproj'
$manifestPath = Join-Path $repoRoot 'HealthChecker.WinUI\Package.appxmanifest'
$appxVersion = Convert-ToAppxVersion -RawVersion $Version
$versionTag = if ($Version.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) { $Version } else { "v$Version" }

$artifactsRoot = Join-Path $repoRoot ("artifacts\$versionTag\installer")
$packageOutDir = Join-Path $artifactsRoot 'package'
$tempCertDir = Join-Path $artifactsRoot 'tmp-cert'

if (Test-Path $artifactsRoot) {
    Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $packageOutDir | Out-Null
New-Item -ItemType Directory -Path $tempCertDir | Out-Null

$cert = $null
$manifestOriginal = Get-Content -LiteralPath $manifestPath -Raw
$pfxPath = Join-Path $tempCertDir 'HealthChecker-Signing.pfx'
$cerPath = Join-Path $packageOutDir ("HealthChecker-$versionTag-x64.cer")

try {
    Write-Host "Patching appx manifest version to $appxVersion"
    $manifestXml = New-Object System.Xml.XmlDocument
    $manifestXml.PreserveWhitespace = $true
    $manifestXml.Load($manifestPath)

    $ns = New-Object System.Xml.XmlNamespaceManager($manifestXml.NameTable)
    $ns.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')

    $identity = $manifestXml.SelectSingleNode('/appx:Package/appx:Identity', $ns)
    if (-not $identity) {
        throw "Package.appxmanifest does not contain Identity node."
    }

    $identity.SetAttribute('Version', $appxVersion)
    $manifestXml.Save($manifestPath)

    Write-Host "Creating temporary MSIX signing certificate ($Publisher)..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Publisher `
        -FriendlyName "HealthChecker MSIX $appxVersion" `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(3)

    $passwordPlain = "HC_${appxVersion}_$(Get-Random -Minimum 100000 -Maximum 999999)!"
    $passwordSecure = ConvertTo-SecureString -String $passwordPlain -AsPlainText -Force

    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $passwordSecure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

    Write-Host 'Building signed MSIX package...'
    dotnet publish $projectPath -c $Configuration `
        -p:Platform=$Platform `
        -p:RuntimeIdentifier=$Runtime `
        -p:WindowsPackageType=MSIX `
        -p:GenerateAppxPackageOnBuild=true `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxBundle=Never `
        -p:AppxPackageVersion=$appxVersion `
        -p:PackageVersion=$appxVersion `
        -p:AppxPackageDir="$packageOutDir\" `
        -p:PackageCertificateKeyFile=$pfxPath `
        -p:PackageCertificatePassword=$passwordPlain `
        -p:AppxPackageSigningEnabled=true

    $builtMsix = Get-ChildItem -LiteralPath $packageOutDir -Recurse -Filter '*.msix' |
        Sort-Object Length -Descending |
        Select-Object -First 1

    if (-not $builtMsix) {
        throw "MSIX package was not produced in $packageOutDir"
    }

    $finalMsixPath = Join-Path $packageOutDir ("HealthChecker-$versionTag-x64.msix")
    Copy-Item -LiteralPath $builtMsix.FullName -Destination $finalMsixPath -Force

    $installScriptPath = Join-Path $packageOutDir ("Install-HealthChecker-$versionTag.ps1")
    $installScript = @"
param(
    [switch]
    `$Force
)

`$ErrorActionPreference = 'Stop'

function Test-IsAdmin {
    `$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    `$principal = New-Object Security.Principal.WindowsPrincipal(`$identity)
    return `$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    Write-Host 'Requesting administrator privileges for certificate installation...'
    `$argList = @(
        '-ExecutionPolicy', 'Bypass',
        '-File', ('\"' + `$PSCommandPath + '\"')
    )
    if (`$Force) { `$argList += '-Force' }

    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList `$argList | Out-Null
    exit
}

`$scriptDir = Split-Path -Parent `$MyInvocation.MyCommand.Path
`$msix = Get-ChildItem -LiteralPath `$scriptDir -Filter '*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
`$cer = Get-ChildItem -LiteralPath `$scriptDir -Filter '*.cer' | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not `$msix) {
    throw 'MSIX package not found next to the installer script.'
}

if (-not `$cer) {
    throw 'Certificate file (.cer) not found next to the installer script.'
}

`$certObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(`$cer.FullName)
`$installedMachineTrustedPeople = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { `$_.Thumbprint -eq `$certObj.Thumbprint }
`$installedMachineRoot = Get-ChildItem Cert:\LocalMachine\Root | Where-Object { `$_.Thumbprint -eq `$certObj.Thumbprint }

if (-not `$installedMachineTrustedPeople) {
    Write-Host 'Installing signing certificate to LocalMachine\\TrustedPeople...'
    Import-Certificate -FilePath `$cer.FullName -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
}

if (-not `$installedMachineRoot) {
    Write-Host 'Installing signing certificate to LocalMachine\\Root...'
    Import-Certificate -FilePath `$cer.FullName -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
}

Write-Host "Installing `$(`$msix.Name)..."
if (`$Force) {
    Add-AppxPackage -Path `$msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion
}
else {
    Add-AppxPackage -Path `$msix.FullName -ForceApplicationShutdown
}

Write-Host 'HealthChecker installation completed.'
"@

    Set-Content -LiteralPath $installScriptPath -Value $installScript -Encoding UTF8

    $zipPath = Join-Path $artifactsRoot ("HealthChecker-$versionTag-msix-installer.zip")
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $packageOutDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

    Write-Host 'Installer artifacts ready:'
    Get-ChildItem -LiteralPath $artifactsRoot -Recurse | Select-Object FullName, Length | Format-Table -AutoSize
}
finally {
    Set-Content -LiteralPath $manifestPath -Value $manifestOriginal -Encoding UTF8

    if ($cert) {
        try {
            Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -Force
        }
        catch {
            Write-Warning "Could not remove temporary certificate from CurrentUser\\My: $($_.Exception.Message)"
        }
    }

    if (Test-Path $pfxPath) {
        Remove-Item -LiteralPath $pfxPath -Force
    }
}
