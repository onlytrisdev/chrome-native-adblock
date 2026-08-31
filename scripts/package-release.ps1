param(
    [Parameter(Mandatory = $false)]
    [ValidatePattern('^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = 'v0.1.0-beta.1'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts\release\$Version"))
$expectedPrefix = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\release')) + [System.IO.Path]::DirectorySeparatorChar
if (-not $releaseRoot.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write outside the release directory: $releaseRoot"
}

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseRoot | Out-Null

Push-Location $projectRoot
try {
    cargo fmt --all -- --check
    cargo test --workspace --locked
    cargo build --workspace --release --locked
    dotnet restore .\ChromeNativeAdblock.slnx
    dotnet test .\ChromeNativeAdblock.slnx -c Release --no-restore --filter 'Category!=Integration'

    $stageRoot = Join-Path $projectRoot "artifacts\stage\$Version"
    $stageRoot = [System.IO.Path]::GetFullPath($stageRoot)
    $stagePrefix = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\stage')) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $stageRoot.StartsWith($stagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside the staging directory: $stageRoot"
    }
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }

    $guiStage = Join-Path $stageRoot 'gui'
    $cliStage = Join-Path $stageRoot 'cli'
    dotnet publish .\gui\ChromeNativeAdblock.Gui\ChromeNativeAdblock.Gui.csproj -c Release -r win-x64 --self-contained true -o $guiStage
    dotnet publish .\launcher\ChromeNativeAdblock.Launcher\ChromeNativeAdblock.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $cliStage

    foreach ($stage in @($guiStage, $cliStage)) {
        Copy-Item -LiteralPath .\target\release\chrome_native_adblock.dll -Destination $stage -Force
        New-Item -ItemType Directory -Path (Join-Path $stage 'filters') -Force | Out-Null
        Copy-Item -LiteralPath .\filters\smoke.txt -Destination (Join-Path $stage 'filters') -Force
        Copy-Item -LiteralPath .\filters\easylist_basic.txt -Destination (Join-Path $stage 'filters') -Force
        Copy-Item -LiteralPath .\filters\youtube_rules.txt -Destination (Join-Path $stage 'filters') -Force
        Copy-Item -LiteralPath .\filters\abpvn_basic.txt -Destination (Join-Path $stage 'filters') -Force
        Copy-Item -LiteralPath .\README.md, .\LICENSE, .\SECURITY.md, .\THIRD_PARTY_NOTICES.md -Destination $stage -Force
    }

    $guiZip = Join-Path $releaseRoot "ChromeNativeAdblock-GUI-$Version-win-x64.zip"
    $cliZip = Join-Path $releaseRoot "ChromeNativeAdblock-CLI-$Version-win-x64.zip"
    Compress-Archive -Path (Join-Path $guiStage '*') -DestinationPath $guiZip -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $cliStage '*') -DestinationPath $cliZip -CompressionLevel Optimal

    $checksumLines = Get-ChildItem -LiteralPath $releaseRoot -Filter '*.zip' -File |
        Sort-Object Name |
        ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
    [System.IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $checksumLines, [System.Text.UTF8Encoding]::new($false))

    Write-Host "Release artifacts created in $releaseRoot"
    Get-ChildItem -LiteralPath $releaseRoot -File | Select-Object Name, Length
}
finally {
    Pop-Location
}
