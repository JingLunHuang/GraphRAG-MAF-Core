param([switch]$Native)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$dotnetExe = Join-Path $workspace '.runtime/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location -LiteralPath $workspace
try {
    $env:DOTNET_CLI_HOME = Join-Path $workspace '.runtime/cli-home'
    & $dotnetExe restore tests/GraphRag.Tests --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
    & $dotnetExe restore src/GraphRag.Cli --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $dotnetExe test tests/GraphRag.Tests -c Release --no-restore --logger 'trx;LogFileName=unit-tests.trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & $dotnetExe run --project src/GraphRag.Cli -c Release --no-restore -- smoke --output artifacts/smoke.json
    if ($LASTEXITCODE -ne 0) { throw 'Workflow smoke failed.' }
    & $dotnetExe run --project src/GraphRag.Cli -c Release --no-restore -- benchmark --output artifacts/vector-benchmark.json
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark failed.' }
    $env:DOTNET_EXE = $dotnetExe
    $env:CLI_DLL = 'src/GraphRag.Cli/bin/Release/net10.0/GraphRag.Cli.dll'
    node scripts/smoke-mcp-stdio.mjs
    if ($LASTEXITCODE -ne 0) { throw 'MCP smoke failed.' }
    if ($Native) {
        & $dotnetExe publish src/GraphRag.Api -c Release -r win-x64 -p:PublishAot=true -p:TrimmerSingleWarn=false -o artifacts/native/win-x64
        if ($LASTEXITCODE -ne 0) { throw 'Native AOT publish failed; install the Visual Studio Desktop C++ build workload.' }
    }
} finally { Pop-Location }
