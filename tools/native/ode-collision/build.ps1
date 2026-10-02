[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OdeSourceArchive,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactsDirectory,

    [switch]$StageForEvaluation
)

$ErrorActionPreference = 'Stop'
$toolRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $toolRoot '..\..\..')).Path
$lock = Get-Content -LiteralPath (Join-Path $toolRoot 'ode-source.lock.json') -Raw | ConvertFrom-Json
$archivePath = (Resolve-Path -LiteralPath $OdeSourceArchive).Path
$artifactRoot = [System.IO.Path]::GetFullPath($ArtifactsDirectory)

$actualArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualArchiveHash -ne $lock.archiveSha256) {
    throw "Pinned ODE source archive SHA-256 mismatch. Expected $($lock.archiveSha256), got $actualArchiveHash."
}

$sourceParent = Join-Path $artifactRoot 'source'
New-Item -ItemType Directory -Force -Path $sourceParent | Out-Null
$expectedSourceRoot = Join-Path $sourceParent 'ode-0.16.6'
if (-not (Test-Path -LiteralPath (Join-Path $expectedSourceRoot 'CMakeLists.txt'))) {
    & tar.exe -xzf $archivePath -C $sourceParent
    if ($LASTEXITCODE -ne 0) { throw "tar extraction failed with exit code $LASTEXITCODE." }
}
$odeSource = (Resolve-Path -LiteralPath $expectedSourceRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $odeSource 'CMakeLists.txt'))) {
    throw 'The verified ODE archive did not contain the expected ode-0.16.6 source root.'
}

$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswherePath)) { throw 'Visual Studio vswhere.exe was not found.' }
$vs2022Roots = @(& $vswherePath -all -version '[17.0,18.0)' -products '*' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($vs2022Roots.Count -eq 0) { throw 'Visual Studio 2022 C++ x64 tools were not found.' }
$vs2022Root = $vs2022Roots[0]

$cmakePath = $null
$cmakeCandidates = [System.Collections.Generic.List[string]]::new()
foreach ($vsRoot in @(& $vswherePath -all -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)) {
    $candidate = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (Test-Path -LiteralPath $candidate) { $cmakeCandidates.Add($candidate) }
}
$pathCmake = Get-Command cmake.exe -ErrorAction SilentlyContinue
if ($pathCmake) { $cmakeCandidates.Add($pathCmake.Source) }
if ($cmakeCandidates.Count -eq 0) { throw 'CMake was not found in a Visual Studio installation or PATH.' }
$cmakePath = $cmakeCandidates[0]
$ctestPath = Join-Path (Split-Path -Parent $cmakePath) 'ctest.exe'
if (-not (Test-Path -LiteralPath $ctestPath)) { throw 'ctest.exe was not found beside the selected CMake installation.' }

$buildRoot = Join-Path $artifactRoot 'build'
$cmakeArgs = @(
    '-S', $toolRoot,
    '-B', $buildRoot,
    '-G', 'Visual Studio 17 2022',
    '-A', 'x64',
    "-DODE_SOURCE_DIR:PATH=$odeSource",
    '-DCMAKE_POLICY_VERSION_MINIMUM:STRING=3.5',
    '-DBUILD_SHARED_LIBS:BOOL=OFF',
    '-DODE_DOUBLE_PRECISION:BOOL=ON',
    '-DODE_WITH_OU:BOOL=ON',
    '-DODE_WITH_DEMOS:BOOL=OFF',
    '-DODE_WITH_TESTS:BOOL=OFF',
    '-DODE_WITH_OPCODE:BOOL=OFF',
    '-DODE_WITH_GIMPACT:BOOL=OFF',
    '-DODE_WITH_LIBCCD:BOOL=OFF',
    '-DCMAKE_MSVC_DEBUG_INFORMATION_FORMAT:STRING=Embedded'
)
& $cmakePath @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed with exit code $LASTEXITCODE." }

& $cmakePath --build $buildRoot --config Release --parallel
if ($LASTEXITCODE -ne 0) { throw "Native ODE wrapper build failed with exit code $LASTEXITCODE." }

& $ctestPath --test-dir $buildRoot -C Release --output-on-failure
if ($LASTEXITCODE -ne 0) { throw "Native ODE contact tests failed with exit code $LASTEXITCODE." }

$builtLibrary = Join-Path $buildRoot 'native\Release\ReAnimated.OdeCollision.dll'
if (-not (Test-Path -LiteralPath $builtLibrary)) {
    $builtLibrary = Join-Path $buildRoot 'native\ReAnimated.OdeCollision.dll'
}
if (-not (Test-Path -LiteralPath $builtLibrary)) { throw 'The native ODE collision DLL was not produced.' }

$packageDirectory = Join-Path $artifactRoot 'bin\win-x64'
New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null
$stagedArtifact = Join-Path $packageDirectory 'ReAnimated.OdeCollision.dll'
Copy-Item -LiteralPath $builtLibrary -Destination $stagedArtifact -Force
$libraryHash = (Get-FileHash -LiteralPath $stagedArtifact -Algorithm SHA256).Hash.ToLowerInvariant()

if ($StageForEvaluation) {
    $resourceDirectory = Join-Path $repoRoot 'src\ReAnimated.Evaluation\Native\win-x64'
    New-Item -ItemType Directory -Force -Path $resourceDirectory | Out-Null
    Copy-Item -LiteralPath $stagedArtifact -Destination (Join-Path $resourceDirectory 'ReAnimated.OdeCollision.dll') -Force
}

$cmakeVersion = (& $cmakePath --version | Select-Object -First 1).Trim()
$vsVersion = (& $vswherePath -latest -version '[17.0,18.0)' -products '*' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion | Select-Object -First 1).Trim()
$wrapperFiles = @('build.ps1', 'README.md', 'CMakeLists.txt', 'ode_collision.cpp', 'ode_collision.h', 'ode_collision_native_tests.cpp', 'ode-source.lock.json')
$wrapperHashes = @{}
foreach ($relativePath in $wrapperFiles) {
    $wrapperHashes[$relativePath] = (Get-FileHash -LiteralPath (Join-Path $toolRoot $relativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
}
$receipt = [ordered]@{
    schemaVersion = 1
    source = $lock.source
    version = $lock.version
    sourceArchiveUrl = $lock.archiveUrl
    sourceArchiveSha256 = $actualArchiveHash
    wrapperAbi = $lock.wrapperAbi
    wrapperInputSha256 = $wrapperHashes
    compilerToolset = $vsVersion
    cmakeVersion = $cmakeVersion
    architecture = 'win-x64'
    configuration = 'Release'
    cmakeOptions = $lock.cmakeOptions
    nativeLibrary = 'ReAnimated.OdeCollision.dll'
    nativeLibrarySha256 = $libraryHash
    nativeTestsPassed = $true
}
$receiptPath = Join-Path $artifactRoot 'ode-collision-build-receipt.json'
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding utf8
Write-Output "Built and native-tested $stagedArtifact"
Write-Output "SHA256 $libraryHash"
Write-Output "Receipt $receiptPath"

