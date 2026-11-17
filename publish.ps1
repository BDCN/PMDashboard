<#
.SYNOPSIS
  Bumps the app version in PMDashboard.csproj, then publishes the release build.

.EXAMPLE
  .\publish.ps1                 # bug-fix release:  1.0.0 -> 1.0.1
  .\publish.ps1 -Bump minor     # feature release:  1.0.1 -> 1.1.0
  .\publish.ps1 -Bump major     # big release:      1.1.0 -> 2.0.0
  .\publish.ps1 -Bump none      # rebuild the current version without changing it
  .\publish.ps1 -Installer      # also build installer\Output\SAT-Setup-<version>.exe (Inno Setup 6)

  Every build that gets installed on any machine should have its own number — that's what
  lets the in-app "newer version in use" banner and the logs tell builds apart.
#>
param(
    [ValidateSet('patch', 'minor', 'major', 'none')]
    [string]$Bump = 'patch',
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$csproj = Join-Path $PSScriptRoot 'PMDashboard.csproj'
$text   = [System.IO.File]::ReadAllText($csproj)

$m = [regex]::Match($text, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if (-not $m.Success) { throw "No <Version>x.y.z</Version> found in $csproj" }
$major = [int]$m.Groups[1].Value; $minor = [int]$m.Groups[2].Value; $patch = [int]$m.Groups[3].Value
$old = "$major.$minor.$patch"

switch ($Bump) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}
$new = "$major.$minor.$patch"

if ($new -ne $old) {
    $text = $text.Substring(0, $m.Index) + "<Version>$new</Version>" + $text.Substring($m.Index + $m.Length)
    # Keep the file's UTF-8 BOM as-is
    [System.IO.File]::WriteAllText($csproj, $text, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host "Version: $old -> $new" -ForegroundColor Cyan
} else {
    Write-Host "Version: $new (unchanged)" -ForegroundColor Cyan
}

Push-Location $PSScriptRoot
try {
    dotnet publish -c Release -r win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

$out = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish'
Write-Host ""
Write-Host "Published SAT v$new to:" -ForegroundColor Green
Write-Host "  $out"
Write-Host "Commit the version bump so the source matches what's installed:"
Write-Host "  git commit -am `"Release v$new`""

if ($Installer) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
              "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 not found - install it from https://jrsoftware.org/isdl.php" }
    & $iscc /Qp "/DAppVersion=$new" (Join-Path $PSScriptRoot 'installer\SAT.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit $LASTEXITCODE)" }
    Write-Host ""
    Write-Host "Installer:" -ForegroundColor Green
    Write-Host "  $(Join-Path $PSScriptRoot "installer\Output\SAT-Setup-$new.exe")"
}