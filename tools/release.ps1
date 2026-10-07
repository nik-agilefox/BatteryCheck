# Выпуск версии: номер в src\Core\AppInfo.cs, тесты, сборка, архив для обновления и установщик в dist\,
# коммит и тег, релиз на GitHub. Программы с этой версией и старше увидят «Update to <версия>».
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 1.0.1 -Notes "What changed"
param(
    [Parameter(Mandatory = $true)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$Version,
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Tool($name, $fallback) {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    if (Test-Path $fallback) { return $fallback }
    throw "$name not found"
}
$git = Tool git "$env:ProgramFiles\Git\cmd\git.exe"
$gh = Tool gh "$env:ProgramFiles\GitHub CLI\gh.exe"

if (& $git status --porcelain) { throw "Uncommitted changes: commit them first, the release is built from a clean tree." }

# 1. Номер версии
$info = Join-Path $root "src\Core\AppInfo.cs"
$text = [IO.File]::ReadAllText($info)
$text = [regex]::Replace($text, 'public const string Version = "[^"]*";', "public const string Version = `"$Version`";")
[IO.File]::WriteAllText($info, $text, (New-Object Text.UTF8Encoding $false))

# 2. Тесты и сборка (запущенная программа держит свой exe — закрыть её)
if (Test-Path "bin\BatteryCheckGui.exe") { Start-Process "bin\BatteryCheckGui.exe" -ArgumentList "--exit" -Wait; Start-Sleep 2 }
& (Join-Path $root "test.cmd")
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
& (Join-Path $root "build.cmd")
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# 3. Файлы релиза: архив для обновления изнутри программы и установщик для нового компьютера
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "BatteryCheck-$Version.zip"
$setup = Join-Path $dist "BatteryCheckSetup-$Version.exe"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "bin\BatteryCheckGui.exe", "bin\BatteryCheck.exe" -DestinationPath $zip
Copy-Item "bin\BatteryCheckSetup.exe" $setup -Force

# 4. Коммит, тег, релиз
if (& $git status --porcelain $info) {
    & $git add $info
    & $git commit -q -m "Release v$Version" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
}
& $git tag "v$Version"
& $git push -q origin HEAD --tags
if (-not $Notes) { $Notes = "Battery Check $Version" }
& $gh release create "v$Version" $zip $setup --title "Battery Check $Version" --notes $Notes
"Released v$Version"
