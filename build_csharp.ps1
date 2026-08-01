[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipTests,
    [switch]$Publish,
    [string]$PublishDirectory = "",
    [string]$CandidateSourceSha256 = "",
    [int]$CandidateInputCount = 0,
    [string]$GitHead = "",
    [ValidateSet("", "clean", "dirty")]
    [string]$GitState = "",
    [string]$SourceIdentity = "",
    [string]$InformationalVersion = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionPath = Join-Path $repositoryRoot "DLReAnimated.slnx"
$appProject = Join-Path $repositoryRoot "src\ReAnimated.App\ReAnimated.App.csproj"
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repositoryRoot "artifacts\csharp\win-x64"
}

$provenanceSupplied =
    -not [string]::IsNullOrWhiteSpace($CandidateSourceSha256) -or
    $CandidateInputCount -ne 0 -or
    -not [string]::IsNullOrWhiteSpace($GitHead) -or
    -not [string]::IsNullOrWhiteSpace($GitState) -or
    -not [string]::IsNullOrWhiteSpace($SourceIdentity) -or
    -not [string]::IsNullOrWhiteSpace($InformationalVersion)
$provenanceArguments = @()
if ($provenanceSupplied) {
    if ($CandidateSourceSha256 -notmatch '^[0-9a-f]{64}$' -or
        $CandidateInputCount -le 0 -or
        $GitHead -notmatch '^[0-9a-f]{40}$' -or
        $GitState -notin @("clean", "dirty") -or
        [string]::IsNullOrWhiteSpace($SourceIdentity) -or
        $SourceIdentity -match '\s' -or
        [string]::IsNullOrWhiteSpace($InformationalVersion) -or
        $InformationalVersion -match '\s') {
        throw "C# build provenance must be supplied as one complete, canonical set."
    }

    $expectedSourceIdentity =
        "dl-reanimated-csharp-source-v1." +
        "git-$GitHead." +
        "state-$GitState." +
        "inputs-$CandidateInputCount." +
        "sha256-$CandidateSourceSha256"
    if ($SourceIdentity -ne $expectedSourceIdentity) {
        throw "C# build source identity does not match its provenance fields."
    }

    $provenanceArguments = @(
        "-p:DlReAnimatedCandidateSourceSha256=$CandidateSourceSha256",
        "-p:DlReAnimatedCandidateInputCount=$CandidateInputCount",
        "-p:DlReAnimatedGitHead=$GitHead",
        "-p:DlReAnimatedGitState=$GitState",
        "-p:DlReAnimatedSourceIdentity=$SourceIdentity",
        "-p:InformationalVersion=$InformationalVersion",
        "-p:IncludeSourceRevisionInInformationalVersion=false",
        "-p:SourceRevisionId=$GitHead",
        "-p:RepositoryCommit=$GitHead")
}

Push-Location $repositoryRoot
try {
    dotnet restore $solutionPath --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE"
    }

    dotnet build `
        $solutionPath `
        --configuration $Configuration `
        --no-restore `
        @provenanceArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    $builtExecutable = Join-Path `
        $repositoryRoot `
        "src\ReAnimated.App\bin\$Configuration\net10.0-windows10.0.19041.0\DLReAnimated.exe"
    if (-not (Test-Path -LiteralPath $builtExecutable -PathType Leaf)) {
        throw "The solution build did not produce the WPF executable: $builtExecutable"
    }
    Write-Host "Built executable $builtExecutable"

    $solutionPublishDirectory = Join-Path `
        $repositoryRoot `
        "artifacts\csharp\solution-build\$Configuration\win-x64"
    $solutionPublishedFiles = @(
        Get-ChildItem `
            -LiteralPath $solutionPublishDirectory `
            -File `
            -Recurse)
    $solutionPublishedDirectories = @(
        Get-ChildItem `
            -LiteralPath $solutionPublishDirectory `
            -Directory `
            -Recurse)
    if ($solutionPublishedFiles.Count -ne 1 `
        -or $solutionPublishedDirectories.Count -ne 0 `
        -or $solutionPublishedFiles[0].Name -ne "DLReAnimated.exe") {
        throw (
            "The solution build must publish exactly one self-contained " +
            "DLReAnimated.exe under $solutionPublishDirectory.")
    }
    Write-Host (
        "Solution build published one self-contained executable: " +
        $solutionPublishedFiles[0].FullName)

    if (-not $SkipTests) {
        dotnet test "tests\ReAnimated.Tests\ReAnimated.Tests.csproj" --configuration $Configuration --no-build
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet test failed with exit code $LASTEXITCODE"
        }
    }

    if ($Publish) {
        if (Test-Path -LiteralPath $PublishDirectory -PathType Container) {
            $existingPublishEntries = @(
                Get-ChildItem -LiteralPath $PublishDirectory -Force)
            if ($existingPublishEntries.Count -ne 0) {
                throw "Single-file publish requires an empty output directory: $PublishDirectory"
            }
        }

        dotnet publish $appProject `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained true `
            --output $PublishDirectory `
            --no-restore `
            -p:DebugSymbols=false `
            -p:DebugType=None `
            @provenanceArguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE"
        }

        $publishedFiles = @(
            Get-ChildItem `
                -LiteralPath $PublishDirectory `
                -File `
                -Recurse)
        $publishedDirectories = @(
            Get-ChildItem `
                -LiteralPath $PublishDirectory `
                -Directory `
                -Recurse)
        if ($publishedFiles.Count -ne 1 `
            -or $publishedDirectories.Count -ne 0 `
            -or $publishedFiles[0].Name -ne "DLReAnimated.exe") {
            throw "Publish must contain exactly one executable named DLReAnimated.exe."
        }

        Write-Host "Published single-file C# application to $($publishedFiles[0].FullName)"
    }
}
finally {
    Pop-Location
}
