param()

$ErrorActionPreference = "Stop"
$LibJxlCommit = "a7a9c787341cf703dede03c2009fa460cae5e5df"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = (Resolve-Path (Join-Path $ScriptDir "../../../..")).Path
$EncoderDir = Join-Path $ScriptDir "BenchmarkEncoder"
$CacheRoot = if ($env:BASIS_PROFILE1_BUILD_CACHE) { $env:BASIS_PROFILE1_BUILD_CACHE } else { Join-Path $env:TEMP "basis-profile1-native-build" }
$LibJxlDir = Join-Path $CacheRoot "libjxl"
$BuildDir = Join-Path $CacheRoot "editor-native-win-x64"
$OutputDir = Join-Path $ProjectRoot "Packages/com.basis.imagesandbox/Plugins/Editor/Windows/x86_64"

function Invoke-Checked([scriptblock]$Command, [string]$Message) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw $Message }
}

New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
if (-not (Test-Path (Join-Path $LibJxlDir ".git"))) {
    Invoke-Checked { git clone https://github.com/libjxl/libjxl.git $LibJxlDir } "Failed to clone libjxl."
}
Invoke-Checked { git -C $LibJxlDir fetch --tags --force origin } "Failed to update libjxl refs."
Invoke-Checked { git -C $LibJxlDir checkout --force $LibJxlCommit } "Failed to checkout pinned libjxl commit."
Invoke-Checked { git -C $LibJxlDir submodule sync --recursive } "Failed to synchronize libjxl submodules."
& git -C $LibJxlDir submodule update --init --recursive --force
if ($LASTEXITCODE -ne 0) {
    Invoke-Checked { git -C $LibJxlDir submodule deinit --force --all } "Failed to reset libjxl submodules."
    Invoke-Checked { git -C $LibJxlDir submodule update --init --recursive --force } "Failed to initialize libjxl submodules."
}

Invoke-Checked {
    cmake -S $EncoderDir -B $BuildDir -A x64 `
        -DLIBJXL_SOURCE_DIR=$LibJxlDir `
        -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL
} "Failed to configure the Profile 1 editor-native codec."
Invoke-Checked { cmake --build $BuildDir --config Release --target basis_profile1_editor --parallel } "Failed to build the Profile 1 editor-native codec."

$Built = Join-Path $BuildDir "Release/basis_profile1_editor.dll"
if (-not (Test-Path $Built)) { throw "Native build succeeded but $Built was not produced." }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Copy-Item -Force $Built (Join-Path $OutputDir "basis_profile1_editor.dll")
Write-Host "Built editor-only Profile 1 native codec: $(Join-Path $OutputDir 'basis_profile1_editor.dll')"
