param(
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

$LibJxlTag = "v0.12.0"
$LibJxlCommit = "a7a9c787341cf703dede03c2009fa460cae5e5df"
$EmscriptenImage = "emscripten/emsdk:4.0.23"
$ExpectedSha256 = "2a08424d9c55af3e4359c932157b03b5a539be11b46ffa9b46e7655e3ede5c39"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $ScriptDir "../../Runtime/Resources/BasisImageSandbox/profile1_decoder.bytes"
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

$CacheRoot = $env:BASIS_PROFILE1_BUILD_CACHE
if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $CacheRoot = Join-Path ([System.IO.Path]::GetTempPath()) "basis-profile1-wasm-build"
}
$CacheRoot = [System.IO.Path]::GetFullPath($CacheRoot)
$LibJxlDir = Join-Path $CacheRoot "libjxl"
$BuildDir = Join-Path $CacheRoot "build"

& docker info | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker Desktop is required and must be running with Linux containers enabled."
}

New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null

if (-not (Test-Path (Join-Path $LibJxlDir ".git"))) {
    & git clone --depth 1 --branch $LibJxlTag --recurse-submodules --shallow-submodules https://github.com/libjxl/libjxl.git $LibJxlDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone pinned libjxl." }
}

$ActualCommit = (& git -C $LibJxlDir rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Failed to read the libjxl revision." }
if ($ActualCommit -ne $LibJxlCommit) {
    throw "Pinned libjxl mismatch: expected $LibJxlCommit, got $ActualCommit"
}

& git -C $LibJxlDir submodule update --init --recursive --depth 1
if ($LASTEXITCODE -ne 0) { throw "Failed to update pinned libjxl submodules." }

if (Test-Path $BuildDir) {
    Remove-Item -Recurse -Force $BuildDir
}
New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

$ProfileMount = "${ScriptDir}:/profile1:ro"
$LibJxlMount = "${LibJxlDir}:/libjxl"
$BuildMount = "${BuildDir}:/build"

& docker run --rm `
    -v $ProfileMount `
    -v $LibJxlMount `
    -v $BuildMount `
    $EmscriptenImage `
    bash -lc "emcmake cmake -S /profile1 -B /build -DCMAKE_BUILD_TYPE=Release -DLIBJXL_SOURCE_DIR=/libjxl && cmake --build /build --target profile1_decoder --parallel"
if ($LASTEXITCODE -ne 0) { throw "Pinned Profile 1 WASM build failed." }

$BuiltWasm = Join-Path $BuildDir "profile1_decoder.wasm"
if (-not (Test-Path $BuiltWasm)) {
    throw "Profile 1 build completed without producing profile1_decoder.wasm."
}
Copy-Item -Force $BuiltWasm $OutputPath

$ActualSha256 = (Get-FileHash -Algorithm SHA256 $OutputPath).Hash.ToLowerInvariant()
if ($ActualSha256 -ne $ExpectedSha256) {
    throw "Profile 1 WASM SHA-256 changed. Expected $ExpectedSha256, got $ActualSha256. Do not update the pin without rerunning native/WASM differential and receiver benchmarks."
}

Write-Output "Built $OutputPath"
Write-Output "SHA-256 $ActualSha256"
