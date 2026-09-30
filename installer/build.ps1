# Builds publish\FrameBurst-Setup-<version>.exe: a self-contained x64 publish packaged with Inno Setup 6.
# -Version overrides the <Version> in FrameBurst.csproj (the release workflow passes the tag, e.g. 1.2.0).
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$version = if ($Version) { $Version } else {
    ([xml](Get-Content "$root\FrameBurst.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

Remove-Item "$root\publish\win-x64" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\FrameBurst.csproj" -c Release -r win-x64 --self-contained true -o "$root\publish\win-x64" "-p:Version=$version"
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

& $iscc "/DAppVersion=$version" "$PSScriptRoot\FrameBurst.iss"
if ($LASTEXITCODE) { throw 'Inno Setup compile failed' }
