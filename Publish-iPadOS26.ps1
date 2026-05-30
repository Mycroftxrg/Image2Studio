param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "ios-arm64",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "Image2Studio.csproj"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot "bin\Release\net10.0-ios\ios-arm64\publish"
}

dotnet restore $project `
    -r $RuntimeIdentifier `
    /p:TargetFramework=net10.0-ios `
    /p:RuntimeIdentifier=$RuntimeIdentifier

dotnet publish $project `
    -f net10.0-ios `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --no-restore `
    /p:RuntimeIdentifier=$RuntimeIdentifier `
    /p:BuildIpa=true `
    /p:IpaPackageDir=$OutputDirectory

$ipaPath = Join-Path $OutputDirectory "Image2Studio.ipa"
if (-not (Test-Path -LiteralPath $ipaPath)) {
    throw "IPA was not created at '$ipaPath'. A Mac build host with Xcode and valid Apple signing assets is required for iOS/iPadOS device packages."
}

Get-Item -LiteralPath $ipaPath
