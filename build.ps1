# Publishes self-contained launchers (no .NET install needed) to .\publish:
#   V Mod Launcher Legacy    -> publish\VModLauncher-Legacy\VModLauncher.Legacy.exe     (GTA V Legacy only)
#   V Mod Launcher Enhanced  -> publish\VModLauncher-Enhanced\VModLauncher.Enhanced.exe (GTA V Enhanced only)
#   V Mod Launcher           -> publish\VModLauncher\VModLauncher.exe                   (both, with a game switcher; -All)
#   Dev kit                  -> publish\VModDevKit\vml.exe + ScriptCheck.exe              (CLI and MCP server; -DevKit)
param([string]$Configuration = "Release", [switch]$All, [switch]$DevKit)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

dotnet test (Join-Path $root "tests\VModLauncher.Tests") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

$builds = @(@{ Edition = "Legacy"; Name = "VModLauncher-Legacy" }, @{ Edition = "Enhanced"; Name = "VModLauncher-Enhanced" })
if ($All) { $builds += @{ Edition = ""; Name = "VModLauncher" } }

foreach ($b in $builds) {
    $out = Join-Path $root "publish\$($b.Name)"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    $args = @("publish", (Join-Path $root "src\VModLauncher\VModLauncher.csproj"), "-c", $Configuration, "-r", "win-x64",
              "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
              "-p:DebugType=none", "-o", $out, "--nologo")
    if ($b.Edition) { $args += "-p:Edition=$($b.Edition)" }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($b.Name)" }

    Copy-Item (Join-Path $root "README.md"), (Join-Path $root "THIRD-PARTY-NOTICES.md") $out
    $zip = Join-Path $root "publish\$($b.Name).zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$out\*" -DestinationPath $zip
    Write-Host "Done: $zip"
}

if ($DevKit) {
    $out = Join-Path $root "publish\VModDevKit"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & dotnet publish (Join-Path $root "tools\vml\vml.csproj") -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for the dev kit" }
    # ScriptCheck runs on the .NET Framework, like the game's Script Hook V .NET, so it ships beside vml.exe
    & dotnet build (Join-Path $root "tools\ScriptCheck\ScriptCheck.csproj") -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for ScriptCheck" }
    Copy-Item (Join-Path $root "tools\ScriptCheck\bin\$Configuration\net48\ScriptCheck.exe*") $out
    Copy-Item (Join-Path $root "docs\devkit.md"), (Join-Path $root "THIRD-PARTY-NOTICES.md") $out
    $zip = Join-Path $root "publish\VModDevKit.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$out\*" -DestinationPath $zip
    Write-Host "Done: $zip"
}
