$ErrorActionPreference = "Stop"
$dependency = Join-Path $PSScriptRoot "Dependencies\CUE4Parse"
$cue4ParseProject = Join-Path $dependency "CUE4Parse\CUE4Parse.csproj"
$conversionProject = Join-Path $dependency "CUE4Parse-Conversion\CUE4Parse-Conversion.csproj"
$cue4ParseCommit = "c8ac62c44685bba305004df3547a3ba3c2ff4e7f"
$cue4ParseRepository = "https://github.com/h4lfheart/CUE4Parse.git"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Install .NET 10 SDK first: https://dotnet.microsoft.com/download/dotnet/10.0"
}

if (-not (Test-Path $cue4ParseProject) -or -not (Test-Path $conversionProject)) {
    if (Test-Path $dependency) {
        throw "CUE4Parse source is incomplete. Extract the full release ZIP again; required file: $conversionProject"
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent $dependency) | Out-Null
    git clone $cue4ParseRepository $dependency
}

# A source-only release archive intentionally has no .git directory. When a Git
# checkout is present, pin it to the tested Fortnite-compatible revision.
if (Test-Path (Join-Path $dependency ".git")) {
    git -C $dependency remote set-url origin $cue4ParseRepository
    git -C $dependency fetch origin $cue4ParseCommit
    git -C $dependency checkout --detach $cue4ParseCommit
}

$localBin = Join-Path $PSScriptRoot "bin"
$localObj = Join-Path $PSScriptRoot "obj"
if (Test-Path $localBin) { Remove-Item $localBin -Recurse -Force }
if (Test-Path $localObj) { Remove-Item $localObj -Recurse -Force }

dotnet publish (Join-Path $PSScriptRoot "ShopStreamExtractor.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:CUE4PARSE_SKIP_NATIVE=true `
    -p:PublishSingleFile=false `
    -o (Join-Path $PSScriptRoot "publish")

if ($LASTEXITCODE -ne 0) {
    throw "Build failed. Copy the errors shown above."
}

Copy-Item (Join-Path $PSScriptRoot "run-current.bat") (Join-Path $PSScriptRoot "publish\run-current.bat") -Force
Copy-Item (Join-Path $PSScriptRoot "run-monitor.bat") (Join-Path $PSScriptRoot "publish\run-monitor.bat") -Force
Copy-Item (Join-Path $PSScriptRoot "README-AR.md") (Join-Path $PSScriptRoot "publish\README-AR.md") -Force
$webhookPath = Join-Path $PSScriptRoot "discord-webhook.txt"
if (Test-Path $webhookPath) {
    Copy-Item $webhookPath (Join-Path $PSScriptRoot "publish\discord-webhook.txt") -Force
} else {
    Write-Warning "discord-webhook.txt was not found; add it to publish before sending Discord messages."
}

$watermarkPath = Join-Path $PSScriptRoot "watermark.png"
if (Test-Path $watermarkPath) {
    Copy-Item $watermarkPath (Join-Path $PSScriptRoot "publish\watermark.png") -Force
} else {
    Write-Warning "watermark.png was not found; rendering will use the program's no-watermark behavior."
}

Write-Host "`nBuild completed: $PSScriptRoot\publish" -ForegroundColor Green
