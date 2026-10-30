param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath($ProjectRoot)
if (-not (Test-Path -LiteralPath (Join-Path $root 'src/ServerWorkbench.Desktop/ServerWorkbench.Desktop.csproj'))) { throw 'Invalid project root.' }
$dist = Join-Path $root 'dist'
$target = Join-Path $dist 'ServerWorkbench'
$staging = Join-Path $dist ('.packaging-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'ServerWorkbench'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
try {
 $source = Join-Path $root 'src/ServerWorkbench.Desktop/bin/Release/net48'
 $binaries = Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.exe','.dll','.config' }
 if (-not ($binaries | Where-Object Name -eq 'ServerWorkbench.exe')) { throw 'Build output is missing.' }
 $binaries | Copy-Item -Destination $payload
 foreach ($relative in @('README.md','docs/使用说明.md','docs/Windows验收.md','docs/测试报告.md','docs/内网穿透.md','THIRD-PARTY-NOTICES.txt','FRP-LICENSE.txt','tools/启动检查.cmd')) {
  Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $payload
 }
 $utf8 = New-Object Text.UTF8Encoding($false)
 $readme = Join-Path $payload 'README.md'
 [IO.File]::WriteAllText($readme, [IO.File]::ReadAllText($readme).Replace('(docs/','('), $utf8)
 [IO.File]::WriteAllText((Join-Path $payload 'README.txt'), "开服工坊 ServerWorkbench 0.1`r`n完整解压后运行 ServerWorkbench.exe。需要64位 Windows 与 .NET Framework 4.8/4.8.1。`r`n实测范围、通过项与未验证项请阅读测试报告.md。`r`n", (New-Object Text.UTF8Encoding($true)))
 # Update only shipped files. Existing Data/settings/logs and all backups are preserved.
 New-Item -ItemType Directory -Path $target -Force | Out-Null
 Get-ChildItem -LiteralPath $payload -File | Copy-Item -Destination $target -Force
 $archive = Join-Path $dist 'ServerWorkbench-win-x64-v0.1.zip'
 $archiveTemp = Join-Path $staging 'portable.zip'
 [IO.Compression.ZipFile]::CreateFromDirectory($payload,$archiveTemp,[IO.Compression.CompressionLevel]::Optimal,$true)
 Copy-Item -LiteralPath $archiveTemp -Destination $archive -Force
 $sourceArchive = Join-Path $dist 'ServerWorkbench-source-v0.1.zip'
 $sourceTemp = Join-Path $staging 'source.zip'
 $zip = [IO.Compression.ZipFile]::Open($sourceTemp,[IO.Compression.ZipArchiveMode]::Create)
 try {
  $pending = New-Object 'Collections.Generic.Stack[string]'
  $pending.Push($root)
  $excluded = @('bin','obj','.git','dist','dotnet','cli-home','nuget','Data','work','__MACOSX','__pycache__')
  while ($pending.Count -gt 0) {
   $directory = $pending.Pop()
   foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
    if ($item.PSIsContainer) { if ($item.Name -notin $excluded) { $pending.Push($item.FullName) }; continue }
    if ($item.Name -eq '.DS_Store') { continue }
    $relative = $item.FullName.Substring($root.Length+1).Replace('\','/')
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$item.FullName,('ServerWorkbench/'+$relative),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
   }
  }
 } finally { $zip.Dispose() }
 Copy-Item -LiteralPath $sourceTemp -Destination $sourceArchive -Force
 function Get-ArchiveDigest([string]$Path) {
  $algorithm = [Security.Cryptography.SHA256]::Create()
  $stream = [IO.File]::OpenRead($Path)
  try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
  finally { $stream.Dispose(); $algorithm.Dispose() }
 }
 $digest = Get-ArchiveDigest $archive
 $sourceDigest = Get-ArchiveDigest $sourceArchive
 [IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), "$digest  $([IO.Path]::GetFileName($archive))`n$sourceDigest  $([IO.Path]::GetFileName($sourceArchive))`n", $utf8)
 $verificationPath = Join-Path $root 'docs/windows-verification.json'
 $verification = $null
 if (Test-Path -LiteralPath $verificationPath) { $verification = Get-Content -LiteralPath $verificationPath -Raw -Encoding UTF8 | ConvertFrom-Json }
 $manifest = [ordered]@{version='0.1';platform='Windows x64';framework='.NET Framework 4.8';archive=[IO.Path]::GetFileName($archive);sha256=$digest;windows_execution_verified=($null -ne $verification -and $verification.desktop_started -eq $true);verification=$verification;files=@(Get-ChildItem -LiteralPath $payload -File | Sort-Object Name | ForEach-Object { @{path=$_.Name;bytes=$_.Length} })}
 [IO.File]::WriteAllText((Join-Path $dist 'build-manifest.json'),($manifest | ConvertTo-Json -Depth 8),$utf8)
 Write-Host "Portable app: $target"
 Write-Host "Archive: $archive"
 Write-Host "SHA256: $digest"
} finally {
 $resolved = [IO.Path]::GetFullPath($staging)
 if (-not $resolved.StartsWith(([IO.Path]::GetFullPath($dist)+'\'),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike '.packaging-*') { throw 'Unsafe staging cleanup path.' }
 if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
