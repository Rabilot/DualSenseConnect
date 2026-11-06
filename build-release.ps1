# Builds the release archives into .\artifacts:
#   DualSenseConnect-<version>-win-x64.zip               small; needs the .NET 10 Desktop Runtime
#   DualSenseConnect-<version>-win-x64-selfcontained.zip larger; runs without installing .NET
# Usage: powershell -ExecutionPolicy Bypass -File .\build-release.ps1

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$project = Get-Content .\DualSenseConnect.csproj
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$out = Join-Path $PSScriptRoot 'artifacts'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $out | Out-Null

$variants = @(
    @{ Name = "DualSenseConnect-$version-win-x64"; Args = @('--self-contained', 'false') },
    @{ Name = "DualSenseConnect-$version-win-x64-selfcontained"
       Args = @('--self-contained', 'true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true') }
)

foreach ($variant in $variants) {
    $dir = Join-Path $out $variant.Name
    dotnet publish -c Release -r win-x64 -o $dir @($variant.Args) -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($variant.Name)" }
    Copy-Item README.md, THIRD-PARTY-NOTICES.md $dir
    if (Test-Path LICENSE) { Copy-Item LICENSE $dir }
    Compress-Archive -Path "$dir\*" -DestinationPath "$dir.zip"
    Remove-Item $dir -Recurse -Force
}

Get-ChildItem $out -Filter *.zip | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    "{0}  {1:N1} MB  SHA256 {2}" -f $_.Name, ($_.Length / 1MB), $hash
}
