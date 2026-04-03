param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "HealthChecker\HealthChecker.csproj"
$artifactsDir = Join-Path $repoRoot ("artifacts\" + $Version)

Write-Host "Preparing release assets for $Version ..."

Get-Process HealthChecker -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $artifactsDir) {
    Remove-Item -LiteralPath $artifactsDir -Recurse -Force
}
New-Item -ItemType Directory -Path $artifactsDir | Out-Null

$publishCommon = @(
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false"
)

dotnet publish $projectPath -c Debug @publishCommon
dotnet publish $projectPath -c Release @publishCommon

$debugExe = Join-Path $repoRoot ("HealthChecker\bin\Debug\net10.0-windows\" + $Runtime + "\publish\HealthChecker.exe")
$releaseExe = Join-Path $repoRoot ("HealthChecker\bin\Release\net10.0-windows\" + $Runtime + "\publish\HealthChecker.exe")
$debugOut = Join-Path $artifactsDir ("HealthChecker-" + $Version + "-debug.exe")
$releaseOut = Join-Path $artifactsDir ("HealthChecker-" + $Version + "-release.exe")
$sourcesZip = Join-Path $artifactsDir ("HealthChecker-" + $Version + "-sources.zip")

Copy-Item -LiteralPath $debugExe -Destination $debugOut -Force
Copy-Item -LiteralPath $releaseExe -Destination $releaseOut -Force

if (git rev-parse --verify --quiet ("refs/tags/" + $Version)) {
    git archive --format=zip --output $sourcesZip $Version
}
else {
    git archive --format=zip --output $sourcesZip HEAD
}

Write-Host "Release assets ready:"
Get-ChildItem -LiteralPath $artifactsDir | Select-Object Name, Length | Format-Table -AutoSize
