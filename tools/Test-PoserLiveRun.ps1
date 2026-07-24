param(
    [string]$Path = "$env:APPDATA\XIVLauncher\pluginConfigs\Poser\live-tests",
    [switch]$Json
)

$ErrorActionPreference = "Stop"

function Find-VerdictFile([string]$Directory) {
    foreach ($name in @("run.json", "report.json")) {
        $candidate = Join-Path $Directory $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }
    return $null
}

function Resolve-VerdictFile([string]$InputPath) {
    if (Test-Path -LiteralPath $InputPath -PathType Leaf) {
        return (Resolve-Path -LiteralPath $InputPath).Path
    }
    if (-not (Test-Path -LiteralPath $InputPath -PathType Container)) {
        throw "Live-test path does not exist: $InputPath"
    }

    $direct = Find-VerdictFile $InputPath
    if ($direct) {
        return (Resolve-Path -LiteralPath $direct).Path
    }

    foreach ($directory in Get-ChildItem -LiteralPath $InputPath -Directory |
            Sort-Object Name -Descending) {
        $candidate = Find-VerdictFile $directory.FullName
        if ($candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "No run.json or compatibility report.json was found beneath: $InputPath"
}

function Require-Properties($Value, [string[]]$Names, [string]$Source) {
    foreach ($name in $Names) {
        if ($null -eq $Value.PSObject.Properties[$name]) {
            throw "Invalid run report; missing '$name': $Source"
        }
    }
}

try {
    $verdictFile = Resolve-VerdictFile $Path
    $raw = Get-Content -LiteralPath $verdictFile -Raw | ConvertFrom-Json
    $isAuthority = $null -ne $raw.PSObject.Properties["outcome"]

    if ($isAuthority) {
        Require-Properties $raw @(
            "schemaVersion",
            "runId",
            "outcome",
            "expectedScenarioExecutions",
            "completedScenarioExecutions",
            "passed",
            "failed",
            "skipped",
            "acceptanceQualified"
        ) $verdictFile

        $outcome = [string]$raw.outcome
        $expected = [int]$raw.expectedScenarioExecutions
        $completed = [int]$raw.completedScenarioExecutions
        $passed = [int]$raw.passed
        $failed = [int]$raw.failed
        $skipped = [int]$raw.skipped
        $acceptance = [bool]$raw.acceptanceQualified
        $detail = [string]$raw.detail
        $runId = [string]$raw.runId
        $sourceFormat = "authority"
    }
    else {
        # Compatibility for artifacts produced before the focused runner began
        # checkpointing run.json. New runs always take the authority branch.
        Require-Properties $raw @(
            "completedUtc",
            "passed",
            "failed",
            "skipped",
            "accepted",
            "results"
        ) $verdictFile

        $passed = [int]$raw.passed
        $failed = [int]$raw.failed
        $skipped = [int]$raw.skipped
        $expected = @($raw.results).Count
        $completed = $passed + $failed + $skipped
        if ($completed -ne $expected) {
            throw "Contradictory legacy counts ($completed/$expected): $verdictFile"
        }
        $outcome = if ($failed -gt 0) {
            "Failed"
        }
        elseif ($skipped -gt 0) {
            "Incomplete"
        }
        else {
            "Succeeded"
        }
        $acceptance = [bool]$raw.accepted
        $detail = "Read from terminal compatibility report."
        $runId = Split-Path -Leaf (Split-Path -Parent $verdictFile)
        $sourceFormat = "legacy-compatibility"
    }

    $terminalOutcomes = @(
        "Succeeded",
        "Failed",
        "Incomplete",
        "Cancelled",
        "Interrupted",
        "RunnerError"
    )
    if ($outcome -ne "Running" -and $outcome -notin $terminalOutcomes) {
        throw "Invalid run outcome '$outcome': $verdictFile"
    }
    if ($outcome -eq "Succeeded" -and (
            $failed -ne 0 -or
            $skipped -ne 0 -or
            $completed -ne $expected)) {
        throw "Contradictory Succeeded verdict: $verdictFile"
    }
    if ($acceptance -and $outcome -ne "Succeeded") {
        throw "Only a Succeeded run can be acceptance-qualified: $verdictFile"
    }

    $verdict = [pscustomobject]@{
        runId = $runId
        outcome = $outcome
        isSuccessful = $outcome -eq "Succeeded"
        acceptanceQualified = $acceptance
        expectedScenarioExecutions = $expected
        completedScenarioExecutions = $completed
        passed = $passed
        failed = $failed
        skipped = $skipped
        detail = $detail
        sourceFormat = $sourceFormat
        verdictFile = $verdictFile
    }

    if ($Json) {
        $verdict | ConvertTo-Json -Depth 10
    }
    else {
        $format =
            "Poser live test {0}: success={1}, acceptance={2}, " +
            "scenarios={3}/{4}, rows={5} passed/{6} failed/{7} skipped"
        Write-Output (
            $format -f
            $verdict.outcome,
            $verdict.isSuccessful.ToString().ToLowerInvariant(),
            $verdict.acceptanceQualified.ToString().ToLowerInvariant(),
            $verdict.completedScenarioExecutions,
            $verdict.expectedScenarioExecutions,
            $verdict.passed,
            $verdict.failed,
            $verdict.skipped
        )
        if ($verdict.detail) {
            Write-Output $verdict.detail
        }
        Write-Output $verdict.verdictFile
    }

    if ($verdict.outcome -eq "Succeeded") {
        exit 0
    }
    if ($verdict.outcome -eq "Running") {
        exit 2
    }
    exit 1
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 3
}
