# Builds a portable, self-contained STB Editor (no .NET install needed) and zips it.
# Output: dist\STB Editor\ (folder) and dist\STBEditor-<version>.zip
param([string]$Version = "0.1.0")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "C:\Program Files\dotnet\dotnet.exe" }
$stage = Join-Path $root "dist\STB Editor"
$zip = Join-Path $root "dist\STBEditor-$Version.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Split-Path $stage) | Out-Null

& $dotnet publish (Join-Path $root "src\Editor.App\Editor.App.csproj") -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -p:SatelliteResourceLanguages=en -o $stage
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Copy-Item (Join-Path $root "README.md") (Join-Path $stage "README.md")
@"
STB Editor $Version

Распаковать в любую папку, например в корень игры: C:\Skyrim\STB Editor\
Запуск: STBEditor.exe (ничего ставить не нужно, .NET уже внутри).

Через MO2: Executables (иконка шестерёнок) -> + -> Add from file -> STBEditor.exe.
Аргументы не нужны. Редактор сам читает профиль MO2 и папки модов, поэтому
запускать через MO2 не обязательно - можно и напрямую.

Если редактор лежит внутри папки игры, он берёт эту игру автоматически.
Настройки, кэш и логи: %LOCALAPPDATA%\STBEditor
"@ | Set-Content -Encoding utf8 (Join-Path $stage "README.txt")

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
"Packed $zip ($size MB)"
