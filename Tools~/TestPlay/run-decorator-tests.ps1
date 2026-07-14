[CmdletBinding()]
param(
    [string]$ProjectPath = (Get-Location).Path,
    [string]$ConfigPath = "",
    [string]$Filter = "UnityDecoScene.DungeonDecorator",
    [switch]$All
)

$ErrorActionPreference = "Stop"

function Find-TestPlay {
    $command = Get-Command testplay -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $go = Get-Command go -ErrorAction SilentlyContinue
    if ($go) {
        $candidate = Join-Path (& go env GOPATH) "bin\testplay.exe"
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    throw "testplay was not found. Install it with: go install github.com/Kubonsang/testplay-runner/cmd/testplay@v0.10.0"
}

function Short-Failure($test) {
    $message = if (-not [string]::IsNullOrWhiteSpace([string]$test.excerpt)) {
        [string]$test.excerpt
    }
    elseif (-not [string]::IsNullOrWhiteSpace([string]$test.message)) {
        [string]$test.message
    }
    else {
        ""
    }
    if ($message.Length -gt 600) { $message = $message.Substring(0, 600) }
    return [ordered]@{
        name = $test.name
        result = $test.result
        message = $message
        file = $test.file
        line = $test.line
    }
}

$project = [IO.Path]::GetFullPath($ProjectPath)
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $project "testplay.decorator.windows.json"
}
$config = [IO.Path]::GetFullPath($ConfigPath)
$testplay = Find-TestPlay

if (-not (Test-Path -LiteralPath $config)) {
    throw "Decorator TestPlay config was not found: $config"
}

Push-Location $project
try {
    $checkRaw = & $testplay --config $config check 2>&1
    $checkExit = $LASTEXITCODE
    if ($checkExit -ne 0) {
        [ordered]@{
            schema_version = "1"
            phase = "check"
            exit_code = $checkExit
            detail = ($checkRaw -join [Environment]::NewLine)
        } | ConvertTo-Json -Compress -Depth 6
        exit $checkExit
    }

    $arguments = @("--config", $config, "run", "--bridge")
    if (-not $All -and -not [string]::IsNullOrWhiteSpace($Filter)) {
        $arguments += @("--filter", $Filter)
    }

    $raw = & $testplay @arguments 2>&1
    $processExit = $LASTEXITCODE
    $joined = $raw -join [Environment]::NewLine
    try {
        $result = $joined | ConvertFrom-Json
    }
    catch {
        [ordered]@{
            schema_version = "1"
            phase = "parse"
            exit_code = 9
            runner_exit_code = $processExit
            detail = $joined.Substring(0, [Math]::Min(2000, $joined.Length))
        } | ConvertTo-Json -Compress -Depth 6
        exit 9
    }

    $summary = [ordered]@{
        schema_version = $result.schema_version
        run_id = $result.run_id
        backend = $result.backend
        exit_code = $result.exit_code
        total = $result.total
        passed = $result.passed
        failed = $result.failed
        skipped = $result.skipped
    }
    if ($result.warnings) { $summary.warnings = @($result.warnings) }
    if ([int]$result.exit_code -ne 0) {
        $summary.errors = @($result.errors | Select-Object -First 20)
        $summary.failed_tests = @($result.tests | Where-Object { $_.result -ne "Passed" } | Select-Object -First 20 | ForEach-Object { Short-Failure $_ })
    }

    $summary | ConvertTo-Json -Compress -Depth 8
    exit [int]$result.exit_code
}
finally {
    Pop-Location
}
