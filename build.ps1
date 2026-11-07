# Сборка БазоНавигатора в dist\<rid>[-light]\ без установки. Нужен .NET SDK 10.
#   .\build.ps1                     автономный вариант для Windows x64 (.NET внутри)
#   .\build.ps1 -Light              лёгкий вариант (нужен установленный .NET 10 Runtime)
#   .\build.ps1 -Runtime linux-x64  другая платформа: win-x64, win-arm64, linux-x64, linux-arm64
#   .\build.ps1 -Test               перед сборкой запустить тесты
param([ValidateSet('win-x64', 'win-x86', 'win-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'linux-musl-arm64', 'osx-x64', 'osx-arm64')]
      [string]$Runtime = 'win-x64', [switch]$Light, [switch]$Test)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Не найден dotnet: установите .NET SDK 10.' }

if ($Test) {
    dotnet test src/StartManager.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли.' }
}

$output = Join-Path 'dist' ($Runtime + $(if ($Light) { '-light' } else { '' }))
# The folder removed before building must stay inside dist.
$dist = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist')) + [IO.Path]::DirectorySeparatorChar
if (-not [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $output)).StartsWith($dist, [StringComparison]::OrdinalIgnoreCase)) { throw 'Недопустимый каталог сборки.' }
# A junction or symbolic link could point anywhere: only a real dist folder is cleaned.
New-Item -ItemType Directory -Force -Path 'dist' | Out-Null
foreach ($path in @('dist', $output)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$path — ссылка на другой каталог; очистка отменена."
    }
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
# SDK 10.0.1xx (Linux VM) reads "--self-contained false" as --self-contained and drops "false": the flag forms only.
$selfContained = if ($Light) { '--no-self-contained' } else { '--self-contained' }
dotnet publish src/StartManager/StartManager.csproj -c Release -r $Runtime $selfContained `
    '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' `
    -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Сборка не выполнена.' }
Get-ChildItem -LiteralPath $output -Recurse -Filter *.pdb | Remove-Item -Force
if (-not $Runtime.StartsWith('win')) { Remove-Item -LiteralPath (Join-Path $output 'Start-with-log.cmd') -ErrorAction SilentlyContinue }
Write-Host ('Готово: ' + $output + $(if ($Light) { ' (нужен .NET 10 Runtime)' } else { '' }))
