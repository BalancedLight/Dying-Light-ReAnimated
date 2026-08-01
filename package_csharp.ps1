[CmdletBinding()]
param(
    [string]$OutputRoot = "",
    [switch]$ProvenanceOnly,
    [switch]$SkipUnavailableOptionalBlenderOracle,
    [switch]$ForcePythonOracle,
    [switch]$ForceAllValidation,
    [string]$PythonOracleRoot = "",
    [string]$BlenderExecutable = ""
)

$ErrorActionPreference = "Stop"

function Get-CandidateInputRelativePaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $resolvedRoot =
        [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    $requiredPrefix =
        $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar
    $paths =
        New-Object "System.Collections.Generic.HashSet[string]" (
            [System.StringComparer]::Ordinal)
    $addFile = {
        param(
            [Parameter(Mandatory = $true)]
            [string]$Path
        )

        $fullPath = [System.IO.Path]::GetFullPath($Path)
        if (-not $fullPath.StartsWith(
                $requiredPrefix,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Candidate input escaped the repository root: $fullPath"
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Required C# candidate input is missing: $fullPath"
        }

        $file = Get-Item -LiteralPath $fullPath -Force
        if (($file.Attributes -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Candidate inputs cannot be filesystem reparse points: $fullPath"
        }

        $relativePath =
            $fullPath.Substring($requiredPrefix.Length)
        $relativePath =
            $relativePath.Replace(
                [System.IO.Path]::DirectorySeparatorChar,
                [char]'/')
        if (-not $paths.Add($relativePath)) {
            return
        }
    }

    foreach ($relativePath in @(
            ".github\workflows\dotnet.yml",
            "DLReAnimated.slnx",
            "Directory.Build.props",
            "Directory.Packages.props",
            "LICENSE",
            "README.md",
            "build_csharp.ps1",
            "global.json",
            "package_csharp.ps1")) {
        & $addFile (Join-Path $resolvedRoot $relativePath)
    }

    foreach ($relativeRoot in @(
            "src",
            "tests\ReAnimated.Tests",
            "tests\fixtures",
            "schemas")) {
        $treeRoot = Join-Path $resolvedRoot $relativeRoot
        if (-not (Test-Path -LiteralPath $treeRoot -PathType Container)) {
            throw "Required C# candidate input directory is missing: $treeRoot"
        }

        foreach ($file in Get-ChildItem `
                     -LiteralPath $treeRoot `
                     -File `
                     -Force `
                     -Recurse) {
            $relative =
                $file.FullName.Substring($requiredPrefix.Length)
            $relative =
                $relative.Replace(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [char]'/')
            if ($relative -match
                '(^|/)(bin|obj|TestResults)(/|$)') {
                continue
            }

            & $addFile $file.FullName
        }
    }

    $docsRoot = Join-Path $resolvedRoot "docs"
    foreach ($file in Get-ChildItem `
                 -LiteralPath $docsRoot `
                 -File `
                 -Force) {
        if ($file.Name -eq "CSHARP_REWRITE.md" -or
            $file.Name -like "DL1_*.md") {
            & $addFile $file.FullName
        }
    }

    $toolsRoot = Join-Path $resolvedRoot "tools"
    foreach ($file in Get-ChildItem `
                 -LiteralPath $toolsRoot `
                 -File `
                 -Force) {
        if ($file.Name -like "validate_dl1_*.ps1" -or
            $file.Name -like "validate_renderer_*.ps1" -or
            $file.Name -eq "validate_csharp.ps1") {
            & $addFile $file.FullName
        }
    }

    [string[]]$result = @($paths)
    [System.Array]::Sort(
        $result,
        [System.StringComparer]::Ordinal)
    return $result
}

function Get-CandidateInputSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string[]]$RelativePaths
    )

    if (-not [System.BitConverter]::IsLittleEndian) {
        throw "The candidate provenance encoder requires a little-endian host."
    }

    $resolvedRoot =
        [System.IO.Path]::GetFullPath($RepositoryRoot)
    $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $hash =
        [System.Security.Cryptography.IncrementalHash]::CreateHash(
            [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $buffer = New-Object byte[] (1024 * 1024)
    try {
        $hash.AppendData(
            $utf8.GetBytes(
                "dl-reanimated-csharp-candidate-input-v1"))
        $hash.AppendData(
            [System.BitConverter]::GetBytes(
                [int]$RelativePaths.Count))
        foreach ($relativePath in $RelativePaths) {
            $pathBytes = $utf8.GetBytes($relativePath)
            $hash.AppendData(
                [System.BitConverter]::GetBytes(
                    [int]$pathBytes.Length))
            $hash.AppendData($pathBytes)

            $fullPath = Join-Path `
                $resolvedRoot `
                $relativePath.Replace(
                    [char]'/',
                    [System.IO.Path]::DirectorySeparatorChar)
            $stream = New-Object System.IO.FileStream(
                $fullPath,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read,
                $buffer.Length,
                [System.IO.FileOptions]::SequentialScan)
            try {
                $declaredLength = $stream.Length
                $hash.AppendData(
                    [System.BitConverter]::GetBytes(
                        [long]$declaredLength))
                $readLength = 0L
                while (($read = $stream.Read(
                            $buffer,
                            0,
                            $buffer.Length)) -ne 0) {
                    $hash.AppendData(
                        $buffer,
                        0,
                        $read)
                    $readLength += $read
                }
                if ($readLength -ne $declaredLength) {
                    throw "Candidate input changed while it was hashed: $relativePath"
                }
            }
            finally {
                $stream.Dispose()
            }
        }

        $hex =
            [System.BitConverter]::ToString(
                $hash.GetHashAndReset())
        $hexWithoutSeparators =
            $hex.Replace(
                "-",
                "")
        return $hexWithoutSeparators.ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function Get-CandidateBuildProvenance {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $git = Get-Command "git" -ErrorAction Stop
    $headOutput = @(
        & $git.Source `
            -C $RepositoryRoot `
            rev-parse `
            --verify `
            HEAD)
    if ($LASTEXITCODE -ne 0 -or
        $headOutput.Count -ne 1) {
        throw "The C# candidate requires one resolvable Git HEAD."
    }

    $gitHead = $headOutput[0].Trim().ToLowerInvariant()
    if ($gitHead -notmatch '^[0-9a-f]{40}$') {
        throw "Git HEAD is not a full 40-character hexadecimal commit."
    }

    $statusOutput = @(
        & $git.Source `
            -C $RepositoryRoot `
            status `
            --porcelain=v1 `
            --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw "Git repository state could not be read."
    }
    $gitState =
        if ($statusOutput.Count -eq 0) {
            "clean"
        }
        else {
            "dirty"
        }

    $relativePaths =
        @(Get-CandidateInputRelativePaths `
            -RepositoryRoot $RepositoryRoot)
    if ($relativePaths.Count -eq 0) {
        throw "The C# candidate provenance input set is empty."
    }
    $candidateSha256 =
        Get-CandidateInputSha256 `
            -RepositoryRoot $RepositoryRoot `
            -RelativePaths $relativePaths

    [xml]$buildProperties = Get-Content `
        -LiteralPath (Join-Path $RepositoryRoot "Directory.Build.props") `
        -Raw
    $versionNodes =
        @($buildProperties.SelectNodes(
            "/Project/PropertyGroup/VersionPrefix"))
    if ($versionNodes.Count -ne 1) {
        throw "Directory.Build.props must define exactly one VersionPrefix."
    }
    $versionPrefix = $versionNodes[0].InnerText.Trim()
    if ($versionPrefix -notmatch
        '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
        throw "VersionPrefix cannot form a package informational version."
    }

    $sourceIdentity =
        "dl-reanimated-csharp-source-v1." +
        "git-$gitHead." +
        "state-$gitState." +
        "inputs-$($relativePaths.Count)." +
        "sha256-$candidateSha256"
    $informationalVersion =
        "$versionPrefix+git.$gitHead." +
        "candidate.$candidateSha256." +
        "inputs.$($relativePaths.Count)." +
        $gitState
    return [pscustomobject]@{
        SchemaVersion = 1
        GitHead = $gitHead
        GitState = $gitState
        CandidateSha256 = $candidateSha256
        CandidateInputCount = $relativePaths.Count
        SourceIdentity = $sourceIdentity
        InformationalVersion = $informationalVersion
    }
}

function Initialize-PackageExecutableVerifier {
    if ($null -ne ("DlReAnimatedPackageExecutableVerifier" -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

public static class DlReAnimatedPackageExecutableVerifier
{
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort Pe32PlusMagic = 0x020b;
    private const ushort WindowsGuiSubsystem = 2;
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;
    private const int RtIcon = 3;
    private const int RtGroupIcon = 14;
    private const int RtVersion = 16;
    private const int RtManifest = 24;

    private static readonly byte[] BundleSignature = new byte[]
    {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    };

    private delegate bool EnumResourceNameCallback(
        IntPtr module,
        IntPtr type,
        IntPtr name,
        IntPtr parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(
        string fileName,
        IntPtr file,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceNames(
        IntPtr module,
        IntPtr type,
        EnumResourceNameCallback callback,
        IntPtr parameter);

    public static void Validate(string path)
    {
        if (String.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An executable path is required.", "path");
        }

        string fullPath = Path.GetFullPath(path);
        ValidateHeadersAndBundle(fullPath);
        ValidateNativeResources(fullPath);
    }

    private static void ValidateHeadersAndBundle(string path)
    {
        using (FileStream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            if (stream.Length < 256 || reader.ReadUInt16() != 0x5a4d)
            {
                throw new InvalidDataException(
                    "The published executable does not have a valid DOS header.");
            }

            stream.Position = 0x3c;
            int peOffset = reader.ReadInt32();
            if (peOffset < 0x40 || peOffset > stream.Length - 96)
            {
                throw new InvalidDataException(
                    "The published executable has an invalid PE header offset.");
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                throw new InvalidDataException(
                    "The published executable does not have a valid PE signature.");
            }

            ushort machine = reader.ReadUInt16();
            if (machine != ImageFileMachineAmd64)
            {
                throw new InvalidDataException(
                    String.Format(
                        "The published executable is not AMD64 (machine 0x{0:x4}).",
                        machine));
            }

            stream.Position = peOffset + 20;
            ushort optionalHeaderSize = reader.ReadUInt16();
            long optionalHeaderOffset = peOffset + 24;
            if (optionalHeaderSize < 70 ||
                optionalHeaderOffset + optionalHeaderSize > stream.Length)
            {
                throw new InvalidDataException(
                    "The published executable has an invalid optional header.");
            }

            stream.Position = optionalHeaderOffset;
            ushort optionalHeaderMagic = reader.ReadUInt16();
            if (optionalHeaderMagic != Pe32PlusMagic)
            {
                throw new InvalidDataException(
                    "The published executable is not a PE32+ image.");
            }

            stream.Position = optionalHeaderOffset + 68;
            ushort subsystem = reader.ReadUInt16();
            if (subsystem != WindowsGuiSubsystem)
            {
                throw new InvalidDataException(
                    String.Format(
                        "The published executable is not a Windows GUI image (subsystem {0}).",
                        subsystem));
            }

            long signatureOffset = FindSequence(stream, BundleSignature);
            if (signatureOffset < sizeof(long))
            {
                throw new InvalidDataException(
                    "The .NET single-file bundle signature is missing.");
            }

            stream.Position = signatureOffset - sizeof(long);
            long bundleHeaderOffset = reader.ReadInt64();
            if (bundleHeaderOffset <= 0 ||
                bundleHeaderOffset >= stream.Length)
            {
                throw new InvalidDataException(
                    "The .NET single-file bundle header was not patched into the app host.");
            }
        }
    }

    private static long FindSequence(Stream stream, byte[] pattern)
    {
        stream.Position = 0;
        byte[] buffer = new byte[64 * 1024 + pattern.Length - 1];
        int carry = 0;
        long consumed = 0;
        while (true)
        {
            int read = stream.Read(
                buffer,
                carry,
                buffer.Length - carry);
            if (read == 0)
            {
                return -1;
            }

            int available = carry + read;
            for (int index = 0; index <= available - pattern.Length; index++)
            {
                bool matches = true;
                for (int patternIndex = 0;
                    patternIndex < pattern.Length;
                    patternIndex++)
                {
                    if (buffer[index + patternIndex] != pattern[patternIndex])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return consumed - carry + index;
                }
            }

            carry = Math.Min(pattern.Length - 1, available);
            Buffer.BlockCopy(
                buffer,
                available - carry,
                buffer,
                0,
                carry);
            consumed += read;
        }
    }

    private static void ValidateNativeResources(string path)
    {
        IntPtr module = LoadLibraryEx(
            path,
            IntPtr.Zero,
            LoadLibraryAsDataFile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The published executable could not be opened as a PE resource image.");
        }

        try
        {
            RequireResourceType(module, RtIcon, "icon image");
            RequireResourceType(module, RtGroupIcon, "icon group");
            RequireResourceType(module, RtVersion, "version");
            RequireResourceType(module, RtManifest, "application manifest");
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private static void RequireResourceType(
        IntPtr module,
        int resourceType,
        string description)
    {
        int count = 0;
        EnumResourceNameCallback callback = delegate(
            IntPtr callbackModule,
            IntPtr callbackType,
            IntPtr callbackName,
            IntPtr callbackParameter)
        {
            count++;
            return true;
        };
        EnumResourceNames(
            module,
            new IntPtr(resourceType),
            callback,
            IntPtr.Zero);
        GC.KeepAlive(callback);
        if (count == 0)
        {
            throw new InvalidDataException(
                "The published executable is missing its " +
                description +
                " resource.");
        }
    }
}
'@
}

function Assert-PackageExecutable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    Initialize-PackageExecutableVerifier
    [DlReAnimatedPackageExecutableVerifier]::Validate(
        [System.IO.Path]::GetFullPath($Path))
}

function Invoke-PackageSelfTest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,
        [Parameter(Mandatory = $true)]
        [string]$OutputDirectory,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedHelperPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedCandidateSha256,
        [Parameter(Mandatory = $true)]
        [int]$ExpectedCandidateInputCount,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedGitHead,
        [Parameter(Mandatory = $true)]
        [ValidateSet("clean", "dirty")]
        [string]$ExpectedGitState,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedSourceIdentity,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedInformationalVersion
    )

    if ($ExpectedCandidateSha256 -notmatch '^[0-9a-f]{64}$' -or
        $ExpectedCandidateInputCount -le 0 -or
        $ExpectedGitHead -notmatch '^[0-9a-f]{40}$' -or
        $ExpectedSourceIdentity -match '\s' -or
        $ExpectedInformationalVersion -match '\s') {
        throw "Package provenance expectations are malformed."
    }

    $resolvedExecutablePath =
        [System.IO.Path]::GetFullPath($ExecutablePath)
    $resolvedSelfTestDirectory =
        [System.IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $resolvedSelfTestDirectory) {
        throw "The package self-test directory must not already exist: $resolvedSelfTestDirectory"
    }
    New-Item `
        -ItemType Directory `
        -Path $resolvedSelfTestDirectory | Out-Null

    $processStartInfo =
        New-Object System.Diagnostics.ProcessStartInfo
    $processStartInfo.FileName = $resolvedExecutablePath
    $processStartInfo.Arguments =
        '--package-self-test "' +
        $resolvedSelfTestDirectory +
        '" ' +
        $ExpectedCandidateSha256 +
        " " +
        $ExpectedCandidateInputCount +
        " " +
        $ExpectedGitHead +
        " " +
        $ExpectedGitState +
        " " +
        $ExpectedSourceIdentity +
        " " +
        $ExpectedInformationalVersion
    $processStartInfo.UseShellExecute = $false
    $processStartInfo.CreateNoWindow = $true
    $processStartInfo.WindowStyle =
        [System.Diagnostics.ProcessWindowStyle]::Hidden
    $process =
        New-Object System.Diagnostics.Process
    $process.StartInfo = $processStartInfo
    try {
        if (-not $process.Start()) {
            throw "The packaged executable self-test process did not start."
        }
        if (-not $process.WaitForExit(60000)) {
            $terminated = $false
            try {
                $process.Kill()
                $terminated = $true
            }
            catch {
                Write-Warning `
                    "Could not terminate the timed-out package self-test: $($_.Exception.Message)"
            }
            if ($terminated -and
                -not $process.WaitForExit(5000)) {
                Write-Warning `
                    "The terminated package self-test did not report exit within five seconds."
            }
            throw "The packaged executable self-test exceeded 60 seconds."
        }
        if ($process.ExitCode -ne 0) {
            throw (
                "The packaged executable self-test failed with exit code " +
                "$($process.ExitCode).")
        }
    }
    finally {
        $process.Dispose()
    }

    $expectedResultName =
        "DL_REANIMATED_PACKAGE_SELF_TEST.json"
    $expectedHelperName =
        "export_dl1_retail_anm2_fbx.py"
    $selfTestFiles = @(
        Get-ChildItem `
            -LiteralPath $resolvedSelfTestDirectory `
            -File `
            -Recurse)
    $selfTestDirectories = @(
        Get-ChildItem `
            -LiteralPath $resolvedSelfTestDirectory `
            -Directory `
            -Recurse)
    $selfTestFileNames = @(
        $selfTestFiles |
            ForEach-Object { $_.Name } |
            Sort-Object)
    $expectedSelfTestFileNames = @(
        $expectedResultName
        $expectedHelperName
    ) | Sort-Object
    if ($selfTestDirectories.Count -ne 0 -or
        (Compare-Object `
            -ReferenceObject $expectedSelfTestFileNames `
            -DifferenceObject $selfTestFileNames)) {
        throw (
            "The package self-test must create exactly its JSON report and " +
            "extracted Blender helper.")
    }

    $resultPath =
        Join-Path $resolvedSelfTestDirectory $expectedResultName
    $helperPath =
        Join-Path $resolvedSelfTestDirectory $expectedHelperName
    $result =
        Get-Content -LiteralPath $resultPath -Raw |
            ConvertFrom-Json
    if ($result.format -ne "dl-reanimated-package-self-test" -or
        [int]$result.schemaVersion -ne 2 -or
        $result.processArchitecture -ne "X64") {
        throw "The package self-test report has an invalid identity or process architecture."
    }
    if (-not [bool]$result.provenanceVerified -or
        [string]$result.candidateSourceSha256 -ne
            $ExpectedCandidateSha256 -or
        [int]$result.candidateInputCount -ne
            $ExpectedCandidateInputCount -or
        [string]$result.gitHead -ne $ExpectedGitHead -or
        [string]$result.gitState -ne $ExpectedGitState -or
        [string]$result.sourceIdentity -ne
            $ExpectedSourceIdentity -or
        [string]$result.assemblyInformationalVersion -ne
            $ExpectedInformationalVersion) {
        throw "The package self-test report does not match the requested source provenance."
    }
    $expectedCliCommands = @(
        "version"
        "inspect-anm2"
        "inspect-fbx"
        "inspect-rpack"
        "inspect-fed"
        "new-project"
        "validate-project"
        "discover-dl1"
        "fingerprint-dl1"
        "index-dl1"
        "build-animation-rpack"
        "export-project"
    )
    $reportedCliCommands = @(
        $result.cliCommands |
            ForEach-Object { [string]$_ })
    $cliCommandDifference = @(
        Compare-Object `
            -ReferenceObject $expectedCliCommands `
            -DifferenceObject $reportedCliCommands)
    if ($result.cliDispatchContract -ne
            "dl-reanimated-cli-dispatch-v1" -or
        $cliCommandDifference.Count -ne 0 -or
        $reportedCliCommands.Count -ne
            $expectedCliCommands.Count) {
        throw "The packaged executable does not report the complete CLI dispatch contract."
    }

    $cliStartInfo =
        New-Object System.Diagnostics.ProcessStartInfo
    $cliStartInfo.FileName = $resolvedExecutablePath
    $cliStartInfo.Arguments = "version"
    $cliStartInfo.UseShellExecute = $false
    $cliStartInfo.CreateNoWindow = $true
    $cliStartInfo.WindowStyle =
        [System.Diagnostics.ProcessWindowStyle]::Hidden
    $cliStartInfo.RedirectStandardOutput = $true
    $cliStartInfo.RedirectStandardError = $true
    $cliProcess =
        New-Object System.Diagnostics.Process
    $cliProcess.StartInfo = $cliStartInfo
    $cliStandardOutput = ""
    $cliStandardError = ""
    $cliExitCode = -1
    try {
        if (-not $cliProcess.Start()) {
            throw "The packaged CLI dispatch process did not start."
        }
        $cliOutputTask =
            $cliProcess.StandardOutput.ReadToEndAsync()
        $cliErrorTask =
            $cliProcess.StandardError.ReadToEndAsync()
        if (-not $cliProcess.WaitForExit(60000)) {
            try {
                $cliProcess.Kill()
                [void]$cliProcess.WaitForExit(5000)
            }
            catch {
                Write-Warning `
                    "Could not terminate the timed-out packaged CLI dispatch: $($_.Exception.Message)"
            }
            throw "The packaged CLI dispatch exceeded 60 seconds."
        }

        $cliStandardOutput =
            $cliOutputTask.GetAwaiter().GetResult()
        $cliStandardError =
            $cliErrorTask.GetAwaiter().GetResult()
        $cliExitCode = $cliProcess.ExitCode
    }
    finally {
        $cliProcess.Dispose()
    }

    if ($cliExitCode -ne 0 -or
        -not [string]::IsNullOrWhiteSpace(
            $cliStandardError) -or
        $cliStandardOutput.Trim() -notmatch
            '^\d+\.\d+\.\d+$') {
        throw (
            "The packaged executable did not dispatch the CLI version " +
            "command with the expected exit code and output.")
    }

    if ([string]::IsNullOrWhiteSpace(
            [string]$result.helperResourceName) -or
        -not ([string]$result.helperResourceName).EndsWith(
            "Blender.export_dl1_retail_anm2_fbx.py",
            [System.StringComparison]::Ordinal)) {
        throw "The package self-test did not identify the embedded Blender helper."
    }

    $helperFile =
        Get-Item -LiteralPath $helperPath
    if ($helperFile.Length -le 0 -or
        [long]$result.helperLength -ne $helperFile.Length) {
        throw "The extracted Blender helper length does not match the package self-test report."
    }
    $actualHelperHash = (Get-FileHash `
        -LiteralPath $helperPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($result.helperSha256 -notmatch '^[0-9a-f]{64}$' -or
        $result.helperSha256 -ne $actualHelperHash) {
        throw "The extracted Blender helper hash does not match the package self-test report."
    }
    $expectedHelperHash = (Get-FileHash `
        -LiteralPath ([System.IO.Path]::GetFullPath(
            $ExpectedHelperPath)) `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHelperHash -ne $expectedHelperHash) {
        throw "The packaged Blender helper does not match the reviewed source helper."
    }

    $requiredResourceSuffixes = @(
        "Embedded.LICENSE"
        "Embedded.README.md"
        "Embedded.Schemas.dlraproj.schema.json"
        "Embedded.Schemas.animation-library-build.schema.json"
        "Embedded.Docs.CSHARP_REWRITE.md"
        "Embedded.Docs.DL1_FIRST_RELEASE_SUPPORT_MATRIX.md"
        "Embedded.Docs.DL1_BLENDER_RETAIL_HANDOFF.md"
        "Embedded.Docs.DL1_WPF_STARTUP_ACCEPTANCE.md"
        "Blender.export_dl1_retail_anm2_fbx.py"
    )
    $reportedResources =
        @($result.requiredResources)
    foreach ($suffix in $requiredResourceSuffixes) {
        $matches = @(
            $reportedResources |
                Where-Object {
                    ([string]$_).EndsWith(
                        $suffix,
                        [System.StringComparison]::Ordinal)
                })
        if ($matches.Count -ne 1) {
            throw "The packaged self-test report must contain exactly one resource ending in '$suffix'."
        }
    }
    if ($reportedResources.Count -ne
        $requiredResourceSuffixes.Count) {
        throw "The packaged self-test report contains an unexpected required-resource set."
    }

    $helperText =
        Get-Content -LiteralPath $helperPath -Raw
    foreach ($marker in @(
            "child_pivot_display_v1",
            "bake_anim_use_all_actions=True",
            "DLR_ACTION_STACKS:",
            "DLR_BIND_POSE:",
            "DLR_ROOT_PARITY:",
            "DLR_EXPORT_COMPLETE:")) {
        if (-not $helperText.Contains($marker)) {
            throw "The extracted packaged Blender helper is missing marker '$marker'."
        }
    }
}

function Get-PythonOracleInputRelativePaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $resolvedRoot =
        [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    $requiredPrefix =
        $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar
    $paths =
        New-Object "System.Collections.Generic.HashSet[string]" (
            [System.StringComparer]::Ordinal)
    $addFile = {
        param(
            [Parameter(Mandatory = $true)]
            [string]$Path
        )

        $fullPath = [System.IO.Path]::GetFullPath($Path)
        if (-not $fullPath.StartsWith(
                $requiredPrefix,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Python oracle input is invalid: $fullPath"
        }

        $relativePath =
            $fullPath.Substring($requiredPrefix.Length).Replace(
                [System.IO.Path]::DirectorySeparatorChar,
                [char]'/')
        [void]$paths.Add($relativePath)
    }

    foreach ($relativePath in @(
            "pyproject.toml",
            "requirements.txt",
            "requirements-dev.txt",
            "requirements-build.txt")) {
        & $addFile (Join-Path $resolvedRoot $relativePath)
    }

    foreach ($file in Get-ChildItem `
                 -LiteralPath $resolvedRoot `
                 -File `
                 -Force) {
        if ($file.Extension -eq ".py") {
            & $addFile $file.FullName
        }
    }

    foreach ($relativeRoot in @(
            "dlanm2_gui",
            "tests",
            "docs\schemas")) {
        $treeRoot = Join-Path $resolvedRoot $relativeRoot
        foreach ($file in Get-ChildItem `
                     -LiteralPath $treeRoot `
                     -File `
                     -Force `
                     -Recurse) {
            $relative =
                $file.FullName.Substring(
                    $requiredPrefix.Length).Replace(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [char]'/')
            if ($relative -match
                    '(^|/)(bin|obj|TestResults|__pycache__|\.pytest_cache)(/|$)' -or
                $relative.StartsWith(
                    "tests/ReAnimated.Tests/",
                    [System.StringComparison]::Ordinal) -or
                $file.Extension -in @(".pyc", ".pyo")) {
                continue
            }

            & $addFile $file.FullName
        }
    }

    # PowerShell validation and packaging wrappers do not participate in
    # Python behavior. Hash only executable Python tooling here so a C#/WPF
    # release-script change cannot force the full Python/Qt oracle to rerun.
    $toolsRoot = Join-Path $resolvedRoot "tools"
    foreach ($file in Get-ChildItem `
                 -LiteralPath $toolsRoot `
                 -File `
                 -Force `
                 -Recurse) {
        if ($file.Extension -eq ".py") {
            & $addFile $file.FullName
        }
    }

    [string[]]$result = @($paths)
    [System.Array]::Sort(
        $result,
        [System.StringComparer]::Ordinal)
    return $result
}

function Get-StringSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $sha256 =
        [System.Security.Cryptography.SHA256]::Create()
    try {
        $hex = [System.BitConverter]::ToString(
            $sha256.ComputeHash(
                $utf8.GetBytes($Value)))
        return $hex.Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-PythonOracleIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string]$PythonPath,
        [string[]]$PythonPrefixArguments = @(),
        [Parameter(Mandatory = $true)]
        [bool]$SkipOptionalBlender
    )

    $relativePaths =
        Get-PythonOracleInputRelativePaths `
            -RepositoryRoot $RepositoryRoot
    $inputSha256 =
        Get-CandidateInputSha256 `
            -RepositoryRoot $RepositoryRoot `
            -RelativePaths $relativePaths
    $environmentScript = @'
import json
import platform
import sys
import numpy
import pytest
import PySide6
print(json.dumps({
    "executable": sys.executable,
    "implementation": platform.python_implementation(),
    "numpy": numpy.__version__,
    "platform": platform.platform(),
    "pyside6": PySide6.__version__,
    "pytest": pytest.__version__,
    "python": platform.python_version(),
}, sort_keys=True, separators=(",", ":")))
'@
    $environmentScriptPath = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        ("dlr-python-environment-{0}.py" -f
            [System.Guid]::NewGuid().ToString("N"))
    try {
        [System.IO.File]::WriteAllText(
            $environmentScriptPath,
            $environmentScript,
            (New-Object System.Text.UTF8Encoding($false)))
        $environmentLines = @(
            & $PythonPath `
                @PythonPrefixArguments `
                $environmentScriptPath
        )
        if ($LASTEXITCODE -ne 0 -or
            $environmentLines.Count -ne 1) {
            throw "Could not fingerprint the Python behavioral-oracle environment."
        }
    }
    finally {
        if (Test-Path `
                -LiteralPath $environmentScriptPath `
                -PathType Leaf) {
            Remove-Item `
                -LiteralPath $environmentScriptPath `
                -Force
        }
    }
    $environmentJson = $environmentLines[0].Trim()
    [void]($environmentJson | ConvertFrom-Json)

    return [pscustomobject][ordered]@{
        Format =
            "dl-reanimated-python-behavioral-oracle-identity-v1"
        ContractVersion = 2
        InputCount = $relativePaths.Count
        InputSha256 = $inputSha256
        EnvironmentSha256 =
            Get-StringSha256 -Value $environmentJson
        SkipOptionalBlender = $SkipOptionalBlender
    }
}

function Test-PythonOracleReceipt {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [psobject]$Identity
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    try {
        $receipt =
            Get-Content -LiteralPath $Path -Raw |
                ConvertFrom-Json
        return (
            $receipt.format -eq
                "dl-reanimated-python-behavioral-oracle-receipt-v1" -and
            $receipt.status -eq "passed" -and
            [int]$receipt.contractVersion -eq
                [int]$Identity.ContractVersion -and
            [int]$receipt.inputCount -eq
                [int]$Identity.InputCount -and
            [string]$receipt.inputSha256 -eq
                [string]$Identity.InputSha256 -and
            [string]$receipt.environmentSha256 -eq
                [string]$Identity.EnvironmentSha256 -and
            [bool]$receipt.skipOptionalBlender -eq
                [bool]$Identity.SkipOptionalBlender -and
            [string]$receipt.validatedAtUtc -match
                '^\d{4}-\d{2}-\d{2}T')
    }
    catch {
        Write-Warning (
            "Ignoring an invalid Python behavioral-oracle receipt: " +
            $_.Exception.Message)
        return $false
    }
}

function Write-PythonOracleReceipt {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [psobject]$Identity,
        [string]$Evidence = "package_csharp.ps1"
    )

    $directory = Split-Path -Parent $Path
    New-Item `
        -ItemType Directory `
        -Path $directory `
        -Force | Out-Null
    $temporaryPath =
        "{0}.{1}.tmp" -f
            $Path,
            [System.Guid]::NewGuid().ToString("N")
    $receipt = [pscustomobject][ordered]@{
        format =
            "dl-reanimated-python-behavioral-oracle-receipt-v1"
        status = "passed"
        contractVersion = [int]$Identity.ContractVersion
        inputCount = [int]$Identity.InputCount
        inputSha256 = [string]$Identity.InputSha256
        environmentSha256 =
            [string]$Identity.EnvironmentSha256
        skipOptionalBlender =
            [bool]$Identity.SkipOptionalBlender
        validatedAtUtc =
            [DateTimeOffset]::UtcNow.ToString("O")
        evidence = $Evidence
    }
    try {
        [System.IO.File]::WriteAllText(
            $temporaryPath,
            ($receipt | ConvertTo-Json -Compress),
            (New-Object System.Text.UTF8Encoding($false)))
        Move-Item `
            -LiteralPath $temporaryPath `
            -Destination $Path `
            -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

$repositoryRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$candidateProvenance =
    Get-CandidateBuildProvenance `
        -RepositoryRoot $repositoryRoot
if ($ProvenanceOnly) {
    $candidateProvenance |
        ConvertTo-Json -Compress
    return
}

if ([string]::IsNullOrWhiteSpace($PythonOracleRoot)) {
    $PythonOracleRoot = Join-Path `
        (Split-Path -Parent $repositoryRoot) `
        "ReAnimated - Python"
}
$resolvedPythonOracleRoot =
    [System.IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables(
            $PythonOracleRoot.Trim().Trim('"'))).TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
$resolvedRepositoryForOracleCheck =
    [System.IO.Path]::GetFullPath($repositoryRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
if ($resolvedPythonOracleRoot.Equals(
        $resolvedRepositoryForOracleCheck,
        [System.StringComparison]::OrdinalIgnoreCase) -or
    $resolvedPythonOracleRoot.StartsWith(
        $resolvedRepositoryForOracleCheck +
            [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The retired Python oracle must remain outside the C# repository."
}
foreach ($requiredOraclePath in @(
        "pyproject.toml",
        "dlanm2_gui",
        "tests",
        "tools")) {
    if (-not (Test-Path -LiteralPath (
            Join-Path $resolvedPythonOracleRoot $requiredOraclePath))) {
        throw (
            "The external Python oracle is incomplete: missing " +
            "'$requiredOraclePath' below $resolvedPythonOracleRoot")
    }
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\csharp"
}

$publishDirectory = Join-Path $OutputRoot "win-x64"
$zipPath = Join-Path $OutputRoot "DL-ReAnimated-CSharp-win-x64.zip"
$hashPath = Join-Path $OutputRoot "SHA256SUMS.txt"
$stageName = ".package-stage-{0}" -f [System.Guid]::NewGuid().ToString("N")
$stageRoot = Join-Path $OutputRoot $stageName
$stagePublishDirectory = Join-Path $stageRoot "win-x64"
$stageZipPath = Join-Path $stageRoot "DL-ReAnimated-CSharp-win-x64.zip"
$stageHashPath = Join-Path $stageRoot "SHA256SUMS.txt"
$stageSelfTestDirectory = Join-Path $stageRoot "package-self-test"
$backupRoot = Join-Path $stageRoot "previous"

$resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($repositoryRoot)
$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$resolvedPublishDirectory = [System.IO.Path]::GetFullPath($publishDirectory)
$resolvedZipPath = [System.IO.Path]::GetFullPath($zipPath)
$resolvedHashPath = [System.IO.Path]::GetFullPath($hashPath)
$resolvedStageRoot = [System.IO.Path]::GetFullPath($stageRoot)
$resolvedStagePublishDirectory =
    [System.IO.Path]::GetFullPath($stagePublishDirectory)
$resolvedStageZipPath = [System.IO.Path]::GetFullPath($stageZipPath)
$resolvedStageHashPath = [System.IO.Path]::GetFullPath($stageHashPath)
$resolvedStageSelfTestDirectory =
    [System.IO.Path]::GetFullPath($stageSelfTestDirectory)
$resolvedBackupRoot = [System.IO.Path]::GetFullPath($backupRoot)
$resolvedFileSystemRoot =
    [System.IO.Path]::GetPathRoot($resolvedOutputRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
if ($resolvedOutputRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) -eq
        $resolvedFileSystemRoot) {
    throw "Output root cannot be a filesystem root."
}
if ($resolvedOutputRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) -eq
        $resolvedRepositoryRoot.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)) {
    throw "Output root cannot be the repository root."
}

$requiredPrefix = $resolvedOutputRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
foreach ($candidate in @(
        $resolvedPublishDirectory,
        $resolvedZipPath,
        $resolvedHashPath,
        $resolvedStageRoot,
        $resolvedStagePublishDirectory,
        $resolvedStageZipPath,
        $resolvedStageHashPath,
        $resolvedStageSelfTestDirectory,
        $resolvedBackupRoot)) {
    if (-not $candidate.StartsWith(
            $requiredPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Every package artifact must remain inside the requested output root."
    }
}
if ([System.IO.Path]::GetFileName($resolvedStageRoot) -ne $stageName) {
    throw "The package staging directory did not resolve to the expected managed name."
}

if (Test-Path -LiteralPath $resolvedOutputRoot -PathType Leaf) {
    throw "The output root is a file, not a directory: $resolvedOutputRoot"
}
New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null
$packageMarker = Join-Path $resolvedOutputRoot "DL_REANIMATED_PACKAGE_ROOT.txt"
$legacyPackageMarker =
    Join-Path $resolvedPublishDirectory "DL_REANIMATED_PACKAGE_ROOT.txt"
if (Test-Path -LiteralPath $packageMarker -PathType Container) {
    throw "The package marker path is a directory, not a file: $packageMarker"
}
if (Test-Path -LiteralPath $resolvedPublishDirectory -PathType Leaf) {
    throw "The final publish path is a file, not a directory: $resolvedPublishDirectory"
}
if (Test-Path -LiteralPath $resolvedZipPath -PathType Container) {
    throw "The final ZIP path is a directory, not a file: $resolvedZipPath"
}
if (Test-Path -LiteralPath $resolvedHashPath -PathType Container) {
    throw "The final checksum path is a directory, not a file: $resolvedHashPath"
}
if (Test-Path -LiteralPath $resolvedPublishDirectory -PathType Container) {
    if (-not (Test-Path -LiteralPath $packageMarker -PathType Leaf) `
        -and -not (Test-Path -LiteralPath $legacyPackageMarker -PathType Leaf)) {
        throw "Refusing to replace an unmarked publish directory: $resolvedPublishDirectory"
    }
}

Set-Content `
    -LiteralPath $packageMarker `
    -Value "Managed by package_csharp.ps1; only the win-x64 child and named package artifacts are replaceable." `
    -Encoding ASCII

New-Item -ItemType Directory -Path $resolvedStageRoot | Out-Null
$replacementCommitted = $false
$rollbackCompleted = $true
try {
    New-Item `
        -ItemType Directory `
        -Path $resolvedStagePublishDirectory | Out-Null

    & (Join-Path $repositoryRoot "build_csharp.ps1") `
        -Configuration Release `
        -SkipTests `
        -Publish `
        -PublishDirectory $resolvedStagePublishDirectory `
        -CandidateSourceSha256 $candidateProvenance.CandidateSha256 `
        -CandidateInputCount $candidateProvenance.CandidateInputCount `
        -GitHead $candidateProvenance.GitHead `
        -GitState $candidateProvenance.GitState `
        -SourceIdentity $candidateProvenance.SourceIdentity `
        -InformationalVersion $candidateProvenance.InformationalVersion
    if ($LASTEXITCODE -ne 0) {
        throw "C# release build failed with exit code $LASTEXITCODE"
    }

    $confirmedProvenance =
        Get-CandidateBuildProvenance `
            -RepositoryRoot $repositoryRoot
    foreach ($propertyName in @(
            "GitHead",
            "GitState",
            "CandidateSha256",
            "CandidateInputCount",
            "SourceIdentity",
            "InformationalVersion")) {
        if ($confirmedProvenance.$propertyName -ne
            $candidateProvenance.$propertyName) {
            throw (
                "C# candidate inputs or repository identity changed " +
                "during the release build; provenance property " +
                "'$propertyName' no longer matches.")
        }
    }

    $publishedFiles = @(
        Get-ChildItem `
            -LiteralPath $resolvedStagePublishDirectory `
            -File `
            -Recurse
    )
    $publishedDirectories = @(
        Get-ChildItem `
            -LiteralPath $resolvedStagePublishDirectory `
            -Directory `
            -Recurse
    )
    if ($publishedFiles.Count -ne 1 `
        -or $publishedDirectories.Count -ne 0 `
        -or $publishedFiles[0].Name -ne "DLReAnimated.exe") {
        throw "The self-contained win-x64 output must contain exactly one DLReAnimated.exe and no companion files."
    }
    $stagedExecutable = $publishedFiles[0].FullName
    Assert-PackageExecutable -Path $stagedExecutable
    Write-Host "[Run] package smoke / packaged-executable-self-test"
    Invoke-PackageSelfTest `
        -ExecutablePath $stagedExecutable `
        -OutputDirectory $resolvedStageSelfTestDirectory `
        -ExpectedHelperPath (Join-Path `
            $repositoryRoot `
            "src\ReAnimated.App\Blender\export_dl1_retail_anm2_fbx.py") `
        -ExpectedCandidateSha256 $candidateProvenance.CandidateSha256 `
        -ExpectedCandidateInputCount $candidateProvenance.CandidateInputCount `
        -ExpectedGitHead $candidateProvenance.GitHead `
        -ExpectedGitState $candidateProvenance.GitState `
        -ExpectedSourceIdentity $candidateProvenance.SourceIdentity `
        -ExpectedInformationalVersion $candidateProvenance.InformationalVersion

    Write-Host "[Run] package smoke / packaged-wpf-animation-library"
    & (Join-Path `
            $repositoryRoot `
            "tools\validate_dl1_wpf_startup.ps1") `
        -ExecutablePath $stagedExecutable
    if ($LASTEXITCODE -ne 0) {
        throw "Packaged WPF startup validation failed with exit code $LASTEXITCODE"
    }

    $csharpValidationParameters = @{
        Tier = "Release"
        Configuration = "Release"
        NoBuild = $true
        CandidateSourceSha256 =
            $candidateProvenance.CandidateSha256
        PythonOracleRoot = $resolvedPythonOracleRoot
    }
    if ($ForceAllValidation) {
        $csharpValidationParameters.ForceAll = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($BlenderExecutable)) {
        $csharpValidationParameters.BlenderExecutable =
            $BlenderExecutable
    }
    if ($SkipUnavailableOptionalBlenderOracle) {
        $csharpValidationParameters.SkipUnavailableOptionalBlender =
            $true
    }
    & (Join-Path $repositoryRoot "tools\validate_csharp.ps1") `
        @csharpValidationParameters
    if ($LASTEXITCODE -ne 0) {
        throw "Tiered C# release validation failed with exit code $LASTEXITCODE"
    }

    $pythonCommand =
        Get-Command "py" -ErrorAction SilentlyContinue
    $pythonPrefixArguments = @()
    if ($null -ne $pythonCommand) {
        $pythonPrefixArguments = @("-3")
    }
    else {
        $pythonCommand =
            Get-Command "python" -ErrorAction Stop
    }

    $isolatedPythonTest =
        "tests/test_unified_gui_regressions.py::test_close_during_animation_import_cancels_then_closes"
    $pythonOracleIdentity =
        Get-PythonOracleIdentity `
            -RepositoryRoot $resolvedPythonOracleRoot `
            -PythonPath $pythonCommand.Source `
            -PythonPrefixArguments $pythonPrefixArguments `
            -SkipOptionalBlender (
                [bool]$SkipUnavailableOptionalBlenderOracle)
    $pythonOracleReceiptPath = Join-Path `
        $repositoryRoot `
        "artifacts\validation\python-behavioral-oracle-v1.json"
    $reusePythonOracle =
        -not $ForcePythonOracle -and
        -not $ForceAllValidation -and
        (Test-PythonOracleReceipt `
            -Path $pythonOracleReceiptPath `
            -Identity $pythonOracleIdentity)
    if ($reusePythonOracle) {
        Write-Host (
            "Reused content-addressed Python behavioral-oracle receipt: " +
            "$pythonOracleReceiptPath")
    }
    else {
        Push-Location $resolvedPythonOracleRoot
        try {
            # PySide6 can abort during this close/cancellation lifecycle
            # regression after hundreds of unrelated Qt tests have shared the
            # same process. The test is not skipped: run every other oracle
            # case first, then run this exact node in a fresh interpreter.
            $pythonOracleArguments = @(
                "-m",
                "pytest",
                "-q",
                "--deselect=$isolatedPythonTest"
            )
            if ($SkipUnavailableOptionalBlenderOracle) {
                $optionalBlenderOracleArguments = @(
                    "--ignore=tests/test_blender_fbx_integration.py",
                    "--ignore=tests/test_dl1_helper_roundtrip_blender.py",
                    "--deselect=tests/test_dl2_anm2_to_fbx.py::test_optional_blender_exports_the_advanced_dl2_scene",
                    "--deselect=tests/test_export_first_private_fixtures.py::test_left_hand_jump_export_first_regression"
                )
                $pythonOracleArguments +=
                    $optionalBlenderOracleArguments
                Write-Warning (
                    "The final Python oracle run is excluding exactly eight " +
                    "optional installed-Blender integration nodes. The exact " +
                    "suite audit, C# mapping audit, parity gates, and all " +
                    "non-Blender oracle nodes remain enabled.")
            }
            & $pythonCommand.Source `
                @pythonPrefixArguments `
                @pythonOracleArguments
            if ($LASTEXITCODE -ne 0) {
                throw (
                    "Python behavioral-oracle main test process failed with " +
                    "exit code $LASTEXITCODE")
            }

            & $pythonCommand.Source `
                @pythonPrefixArguments `
                -m pytest -q `
                $isolatedPythonTest
            if ($LASTEXITCODE -ne 0) {
                throw (
                    "Python behavioral-oracle isolated Qt lifecycle test " +
                    "failed with exit code $LASTEXITCODE")
            }
        }
        finally {
            Pop-Location
        }

        Write-PythonOracleReceipt `
            -Path $pythonOracleReceiptPath `
            -Identity $pythonOracleIdentity
        Write-Host (
            "Recorded content-addressed Python behavioral-oracle receipt: " +
            "$pythonOracleReceiptPath")
    }

    Compress-Archive `
        -LiteralPath $stagedExecutable `
        -DestinationPath $resolvedStageZipPath `
        -CompressionLevel Optimal
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($resolvedStageZipPath)
    try {
        if ($zip.Entries.Count -ne 1 `
            -or $zip.Entries[0].FullName -ne "DLReAnimated.exe") {
            throw "The ZIP must contain exactly one DLReAnimated.exe entry."
        }
    }
    finally {
        $zip.Dispose()
    }
    $executableHash = (Get-FileHash `
        -LiteralPath $stagedExecutable `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipHash = (Get-FileHash `
        -LiteralPath $resolvedStageZipPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashLines = @(
        "# dl-reanimated-provenance-schema: 1"
        "# git-head: {0}" -f $candidateProvenance.GitHead
        "# git-state: {0}" -f $candidateProvenance.GitState
        "# candidate-input-count: {0}" -f $candidateProvenance.CandidateInputCount
        "# candidate-source-sha256: {0}" -f $candidateProvenance.CandidateSha256
        "# source-identity: {0}" -f $candidateProvenance.SourceIdentity
        "# assembly-informational-version: {0}" -f $candidateProvenance.InformationalVersion
        "{0} *win-x64/DLReAnimated.exe" -f $executableHash
        "{0} *{1}" -f $zipHash, [System.IO.Path]::GetFileName($resolvedZipPath)
    )
    Set-Content `
        -LiteralPath $resolvedStageHashPath `
        -Value $hashLines `
        -Encoding ASCII

    New-Item -ItemType Directory -Path $resolvedBackupRoot | Out-Null
    $backupPublishDirectory =
        Join-Path $resolvedBackupRoot "win-x64"
    $backupZipPath =
        Join-Path $resolvedBackupRoot ([System.IO.Path]::GetFileName($resolvedZipPath))
    $backupHashPath =
        Join-Path $resolvedBackupRoot ([System.IO.Path]::GetFileName($resolvedHashPath))
    $newPublishInstalled = $false
    $newZipInstalled = $false
    $newHashInstalled = $false
    $publishBackedUp = $false
    $zipBackedUp = $false
    $hashBackedUp = $false
    $rollbackCompleted = $false
    try {
        if (Test-Path -LiteralPath $resolvedPublishDirectory -PathType Container) {
            Move-Item `
                -LiteralPath $resolvedPublishDirectory `
                -Destination $backupPublishDirectory
            $publishBackedUp = $true
        }
        if (Test-Path -LiteralPath $resolvedZipPath -PathType Leaf) {
            Move-Item `
                -LiteralPath $resolvedZipPath `
                -Destination $backupZipPath
            $zipBackedUp = $true
        }
        if (Test-Path -LiteralPath $resolvedHashPath -PathType Leaf) {
            Move-Item `
                -LiteralPath $resolvedHashPath `
                -Destination $backupHashPath
            $hashBackedUp = $true
        }

        Move-Item `
            -LiteralPath $resolvedStagePublishDirectory `
            -Destination $resolvedPublishDirectory
        $newPublishInstalled = $true
        Move-Item `
            -LiteralPath $resolvedStageZipPath `
            -Destination $resolvedZipPath
        $newZipInstalled = $true
        Move-Item `
            -LiteralPath $resolvedStageHashPath `
            -Destination $resolvedHashPath
        $newHashInstalled = $true
        $replacementCommitted = $true
    }
    catch {
        $replacementFailure = $_.Exception
        $rollbackErrors =
            New-Object "System.Collections.Generic.List[string]"

        try {
            if ($newHashInstalled -and
                (Test-Path -LiteralPath $resolvedHashPath -PathType Leaf)) {
                Remove-Item -LiteralPath $resolvedHashPath -Force
            }
        }
        catch {
            $rollbackErrors.Add(
                "Could not remove the new checksum file: $($_.Exception.Message)")
        }
        try {
            if ($newZipInstalled -and
                (Test-Path -LiteralPath $resolvedZipPath -PathType Leaf)) {
                Remove-Item -LiteralPath $resolvedZipPath -Force
            }
        }
        catch {
            $rollbackErrors.Add(
                "Could not remove the new ZIP file: $($_.Exception.Message)")
        }
        try {
            if ($newPublishInstalled -and
                (Test-Path -LiteralPath $resolvedPublishDirectory -PathType Container)) {
                Remove-Item `
                    -LiteralPath $resolvedPublishDirectory `
                    -Recurse `
                    -Force
            }
        }
        catch {
            $rollbackErrors.Add(
                "Could not remove the new publish directory: $($_.Exception.Message)")
        }

        try {
            if ($publishBackedUp) {
                if (-not (Test-Path `
                        -LiteralPath $backupPublishDirectory `
                        -PathType Container)) {
                    throw "The previous publish-directory backup is missing."
                }
                if (Test-Path -LiteralPath $resolvedPublishDirectory) {
                    throw "The final publish path is still occupied."
                }
                Move-Item `
                    -LiteralPath $backupPublishDirectory `
                    -Destination $resolvedPublishDirectory
                $publishBackedUp = $false
            }
        }
        catch {
            $rollbackErrors.Add(
                "Could not restore the previous publish directory: $($_.Exception.Message)")
        }
        try {
            if ($zipBackedUp) {
                if (-not (Test-Path `
                        -LiteralPath $backupZipPath `
                        -PathType Leaf)) {
                    throw "The previous ZIP backup is missing."
                }
                if (Test-Path -LiteralPath $resolvedZipPath) {
                    throw "The final ZIP path is still occupied."
                }
                Move-Item `
                    -LiteralPath $backupZipPath `
                    -Destination $resolvedZipPath
                $zipBackedUp = $false
            }
        }
        catch {
            $rollbackErrors.Add(
                "Could not restore the previous ZIP file: $($_.Exception.Message)")
        }
        try {
            if ($hashBackedUp) {
                if (-not (Test-Path `
                        -LiteralPath $backupHashPath `
                        -PathType Leaf)) {
                    throw "The previous checksum backup is missing."
                }
                if (Test-Path -LiteralPath $resolvedHashPath) {
                    throw "The final checksum path is still occupied."
                }
                Move-Item `
                    -LiteralPath $backupHashPath `
                    -Destination $resolvedHashPath
                $hashBackedUp = $false
            }
        }
        catch {
            $rollbackErrors.Add(
                "Could not restore the previous checksum file: $($_.Exception.Message)")
        }

        if ($rollbackErrors.Count -eq 0) {
            $rollbackCompleted = $true
            throw $replacementFailure
        }

        $rollbackSummary = $rollbackErrors -join " "
        $message =
            "Package replacement failed and rollback was incomplete. " +
            "Recovery data was preserved at '$resolvedStageRoot'. " +
            "Original failure: $($replacementFailure.Message) " +
            "Rollback failures: $rollbackSummary"
        $wrappedFailure = New-Object `
            -TypeName System.InvalidOperationException `
            -ArgumentList @($message, $replacementFailure)
        throw $wrappedFailure
    }

    Write-Host "Created $(Join-Path $resolvedPublishDirectory 'DLReAnimated.exe')"
    Write-Host "Created $resolvedZipPath"
    Write-Host "Created $resolvedHashPath"
}
finally {
    if (($replacementCommitted -or $rollbackCompleted) -and
        (Test-Path -LiteralPath $resolvedStageRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedStageRoot -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $resolvedStageRoot -PathType Container) {
        Write-Warning `
            "Package staging and recovery data were preserved at '$resolvedStageRoot'."
    }
}
