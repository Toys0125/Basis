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

function Find-CMake {
    $command = Get-Command cmake.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
    if (Test-Path $vswhere) {
        $installations = & $vswhere -products * -property installationPath
        foreach ($installation in $installations) {
            if ([string]::IsNullOrWhiteSpace($installation)) { continue }
            $candidate = Join-Path $installation "Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
            if (Test-Path $candidate) { return $candidate }
        }
    }

    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        foreach ($edition in @("Community", "Professional", "Enterprise", "BuildTools")) {
            $candidate = Join-Path $root "Microsoft Visual Studio/2022/$edition/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
            if (Test-Path $candidate) { return $candidate }
        }
    }

    throw "CMake was not found. Install the Visual Studio 'C++ CMake tools for Windows' component (or a standalone CMake) and retry."
}

$CMake = Find-CMake
Write-Host "Using CMake: $CMake"

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

$LibJxlCMakeArgument = "-DLIBJXL_SOURCE_DIR=$LibJxlDir"
$MsvcRuntimeArgument = "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL"
Invoke-Checked {
    & $CMake -S $EncoderDir -B $BuildDir -A x64 `
        $LibJxlCMakeArgument `
        $MsvcRuntimeArgument
} "Failed to configure the Profile 1 editor-native codec."
Invoke-Checked { & $CMake --build $BuildDir --config Release --target basis_profile1_editor --parallel } "Failed to build the Profile 1 editor-native codec."

$Built = Join-Path $BuildDir "Release/basis_profile1_editor.dll"
if (-not (Test-Path $Built)) { throw "Native build succeeded but $Built was not produced." }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Copy-Item -Force $Built (Join-Path $OutputDir "basis_profile1_editor.dll")
Write-Host "Built editor-only Profile 1 native codec: $(Join-Path $OutputDir 'basis_profile1_editor.dll')"
