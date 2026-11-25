<#
.SYNOPSIS
Windowsの開発PCまたはGitHub ActionsでIIS配備用ZIPを生成する。
.NOTES
本番サーバー上では実行しない。
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location $PSScriptRoot
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
$dist = Join-Path $PSScriptRoot 'dist'
$site = Join-Path $dist 'site'
$test = Join-Path $dist 'test'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item $site, $test -ItemType Directory -Force | Out-Null
Copy-Item 'src/frontend/*' $site -Recurse -Force
$bin = Join-Path $site 'bin'
New-Item $bin -ItemType Directory -Force | Out-Null

# ビルド時にだけ外部パッケージを取得する。サーバーへの追加導入は不要。
dotnet build 'src/backend/InfoBoard.csproj' -c Release -o $bin
if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }
dotnet build 'tests/InfoBoard.Smoke.csproj' -c Release -o $test
if ($LASTEXITCODE -ne 0) { throw 'Smoke test build failed.' }

# System.Data.SQLiteのネイティブDLLを確実に同梱する（64ビットIIS）。
$interop = Get-ChildItem (Join-Path $env:NUGET_PACKAGES 'stub.system.data.sqlite.core.netframework/1.0.119') -Filter 'SQLite.Interop.dll' -Recurse | Where-Object { $_.Directory.Name -eq 'x64' -and $_.FullName -match 'net46|net45' } | Select-Object -First 1
if (-not $interop) { throw 'SQLite x64 interop library not found.' }
foreach ($target in @($bin, $test)) {
  $native = Join-Path $target 'x64'
  New-Item $native -ItemType Directory -Force | Out-Null
  Copy-Item $interop.FullName (Join-Path $native 'SQLite.Interop.dll') -Force
}
& (Join-Path $test 'InfoBoard.Smoke.exe')
if ($LASTEXITCODE -ne 0) { throw 'SQLite smoke tests failed.' }

node --check 'src/frontend/app.js'
if ($LASTEXITCODE -ne 0) { throw 'JavaScript syntax check failed.' }
node --test 'tests/frontend.test.cjs'
if ($LASTEXITCODE -ne 0) { throw 'Frontend regression tests failed.' }
# DBは初回起動時に生成する。既存DBは再配備で上書きしない。
$notes = Join-Path $dist 'deployment.md'
Copy-Item 'docs/deployment.md' $notes
$required = @('index.html','web.config','Global.asax','app.js','style.css','bin/InfoBoard.dll','bin/System.Web.Http.dll','bin/System.Web.Http.WebHost.dll','bin/System.Data.SQLite.dll','bin/x64/SQLite.Interop.dll')
foreach ($item in $required) {
  if (-not (Test-Path (Join-Path $site $item))) { throw "Missing deployment file: $item" }
}
Compress-Archive -Path (Join-Path $site '*') -DestinationPath (Join-Path $dist 'InfoBoard-IIS.zip') -Force
Write-Host 'Generated dist/InfoBoard-IIS.zip. Run IIS acceptance checks before production.'
