param(
    [Parameter(Mandatory = $true)][string]$InputDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

$LibJxlTag = "v0.12.0"
$LibJxlCommit = "a7a9c787341cf703dede03c2009fa460cae5e5df"
$EmscriptenImage = "emscripten/emsdk:4.0.23"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$EncoderDir = Join-Path $ScriptDir "BenchmarkEncoder"

& docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Desktop is required and must be running." }

$CacheRoot = $env:BASIS_PROFILE1_BUILD_CACHE
if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $CacheRoot = Join-Path ([System.IO.Path]::GetTempPath()) "basis-profile1-wasm-build"
}
$CacheRoot = [System.IO.Path]::GetFullPath($CacheRoot)
$LibJxlDir = Join-Path $CacheRoot "libjxl"
$BuildDir = Join-Path $CacheRoot "benchmark-encoder-native"
$InputDirectory = [System.IO.Path]::GetFullPath($InputDirectory)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

New-Item -ItemType Directory -Force -Path $CacheRoot, $BuildDir, $OutputDirectory | Out-Null

if (-not (Test-Path (Join-Path $LibJxlDir ".git"))) {
    & git clone --depth 1 --branch $LibJxlTag --recurse-submodules --shallow-submodules https://github.com/libjxl/libjxl.git $LibJxlDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone pinned libjxl." }
}
$ActualCommit = (& git -C $LibJxlDir rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $ActualCommit -ne $LibJxlCommit) {
    throw "Pinned libjxl mismatch: expected $LibJxlCommit, got $ActualCommit"
}
& git -C $LibJxlDir submodule update --init --recursive --depth 1
if ($LASTEXITCODE -ne 0) { throw "Failed to update pinned libjxl submodules." }

$EncoderMount = "${EncoderDir}:/encoder:ro"
$LibJxlMount = "${LibJxlDir}:/libjxl:ro"
$BuildMount = "${BuildDir}:/build"
$InputMount = "${InputDirectory}:/input:ro"
$OutputMount = "${OutputDirectory}:/output"

& docker run --rm `
    -v $EncoderMount `
    -v $LibJxlMount `
    -v $BuildMount `
    -v $InputMount `
    -v $OutputMount `
    $EmscriptenImage `
    bash -lc "CC=gcc CXX=g++ cmake -S /encoder -B /build -DCMAKE_BUILD_TYPE=Release -DLIBJXL_SOURCE_DIR=/libjxl && cmake --build /build --target profile1_benchmark_encoder --parallel && /build/profile1_benchmark_encoder /input /output"
if ($LASTEXITCODE -ne 0) { throw "GIF benchmark conversion failed." }
