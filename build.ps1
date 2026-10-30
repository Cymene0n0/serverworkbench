$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'tools/cli-home'
$taskDotnet = Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) {
 if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET SDK 10 from https://dotnet.microsoft.com/download/dotnet/10.0 to build. End users only need .NET Framework 4.8.' }
 $taskDotnet = (Get-Command dotnet).Source
}
& $taskDotnet run --project tests/ServerWorkbench.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
& $taskDotnet build src/ServerWorkbench.Desktop/ServerWorkbench.Desktop.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
& (Join-Path $PSScriptRoot 'tools/package.ps1')
