<#
.SYNOPSIS
Builds a GRID Autosport VR release package: installer, portable ZIP and SHA256SUMS.txt.

.DESCRIPTION
Builds the 32-bit bridge (renderer\gasxr.dll) and OpenXR probe from source, publishes the launcher
with its version, channel, build time and source revision stamped, stages them as
GRIDAutosportVR\ with the README, CHANGELOG, OpenXR licenses and a package.json of SHA-256 hashes,
then writes GRIDAutosportVR-<version>.zip, GRIDAutosportVR-<version>-Setup.exe (Inno Setup 7) and
SHA256SUMS.txt to artifacts\packages\<version>-<time>.

The source must be committed: a package records its commit. -AllowDirty packages uncommitted
changes for local testing only; its revision is marked <commit>-dirty and it must not be published.
The top CHANGELOG.md section must be for -Version; it becomes the installer's release notes.

.EXAMPLE
pwsh -File tools\Package.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [ValidateSet('Stable', 'Experimental')][string]$Channel = 'Experimental',
    [string]$OpenXrSdk = (Join-Path $PSScriptRoot '..\..\GRID2VR\.deps\OpenXR-SDK'),
    [string]$InnoCompiler = "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Tool output goes to build\package\package.log; it is shown only when a step fails.
$log = Join-Path $root 'build\package\package.log'
New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
Set-Content -LiteralPath $log -Value "GRID Autosport VR package log $(Get-Date -Format o)" -Encoding utf8
function Invoke-Checked([string]$what, [scriptblock]$command) {
    Add-Content -LiteralPath $log -Value "`n== $what" -Encoding utf8
    & $command *>&1 | Add-Content -LiteralPath $log -Encoding utf8
    if ($LASTEXITCODE) {
        Get-Content -LiteralPath $log -Tail 40 | Write-Host
        throw "$what failed (exit $LASTEXITCODE). Full log: $log"
    }
}

# Source revision: every package names the commit it was built from.
$commit = (git -C $root rev-parse --short=12 HEAD)
if ($LASTEXITCODE -or -not $commit) { throw 'Could not read the git commit.' }
$dirty = [bool](git -C $root status --porcelain --untracked-files=normal)
if ($dirty -and -not $AllowDirty) { throw 'The working tree has uncommitted changes. Commit them, or pass -AllowDirty for a local test package (marked -dirty; never publish it).' }
$revision = $commit + $(if ($dirty) { '-dirty' } else { '' })

# Release notes: the top CHANGELOG section, which must describe this version.
$changelog = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'))
$section = [regex]::Match($changelog, '(?ms)^## (?<head>[^\r\n]+)\r?\n(?<body>.*?)(?=^## |\z)')
if (-not $section.Success -or $section.Groups['head'].Value -notmatch ('^' + [regex]::Escape($Version) + '(\s|$)')) {
    throw "The top CHANGELOG.md section must start with '## $Version'."
}
if (-not (Test-Path -LiteralPath (Join-Path $OpenXrSdk 'include\openxr\openxr.h'))) { throw "OpenXR-SDK checkout not found: $OpenXrSdk" }
if (-not (Test-Path -LiteralPath $InnoCompiler)) { throw "Inno Setup 7 compiler not found: $InnoCompiler" }

$builtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm') + ' UTC'
$output = Join-Path $root ("artifacts\packages\$Version-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$build = Join-Path $root 'build\package'
$stage = Join-Path $output 'GRIDAutosportVR'
New-Item -ItemType Directory -Path "$stage\renderer", "$stage\probe", "$stage\licenses\OpenXR" -Force | Out-Null
Write-Host "Packaging GRID Autosport VR $Version $Channel ($revision) into $output"

# 32-bit native parts, built from the current source.
foreach ($native in @(@{ Folder = 'bridge'; Target = 'gasxr' }, @{ Folder = 'probe'; Target = 'AutosportXRProbe' })) {
    $binary = Join-Path $build $native.Folder
    Invoke-Checked "Configure $($native.Folder)" { cmake -S (Join-Path $root $native.Folder) -B $binary -G 'Visual Studio 17 2022' -A Win32 "-DOPENXR_SDK=$OpenXrSdk" }
    Invoke-Checked "Build $($native.Folder)" { cmake --build $binary --config Release --target $native.Target -- /m /v:minimal }
}
Copy-Item -LiteralPath (Join-Path $build 'bridge\Release\gasxr.dll') -Destination "$stage\renderer"
Copy-Item -LiteralPath (Join-Path $build 'probe\Release\AutosportXRProbe.exe') -Destination "$stage\probe"
# The launcher's controller / wheel reader (DirectInput + XInput, from GRID2VR), x64 like the launcher.
$drivingInput = Join-Path $build 'driving-input'
Invoke-Checked 'Configure driving-input' { cmake -S (Join-Path $root 'tools\driving-input') -B $drivingInput -G 'Visual Studio 17 2022' -A x64 }
Invoke-Checked 'Build driving-input' { cmake --build $drivingInput --config Release --target driving_input -- /m /v:minimal }
Copy-Item -LiteralPath (Join-Path $drivingInput 'Release\driving_input.dll') -Destination $stage

# Launcher, stamped for Help / About and future update checks.
$publish = Join-Path $build 'launcher'
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
Invoke-Checked 'Launcher publish' {
    dotnet publish (Join-Path $root 'launcher\GRIDAutosportVR.csproj') -c Release -o $publish --nologo -v q `
        "-p:Version=$Version" "-p:ReleaseChannel=$Channel" "-p:BuildUtc=$builtUtc" "-p:BuildRevision=$revision"
}
Copy-Item -LiteralPath (Join-Path $publish 'GRIDAutosportVR.exe') -Destination $stage
$stamped = (Get-Item -LiteralPath "$stage\GRIDAutosportVR.exe").VersionInfo.ProductVersion
if (-not $stamped.StartsWith($Version)) { throw "The launcher reports version '$stamped', not $Version." }

Copy-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md') -Destination $stage
# The OpenXR loader is linked into the bridge and probe.
Copy-Item -LiteralPath (Join-Path $OpenXrSdk 'LICENSE') -Destination "$stage\licenses\OpenXR\LICENSE.txt"
Get-ChildItem -LiteralPath (Join-Path $OpenXrSdk 'LICENSES') -File | Copy-Item -Destination "$stage\licenses\OpenXR"

$utf8 = New-Object System.Text.UTF8Encoding($false)
$files = [ordered]@{}
Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    $files[$_.FullName.Substring($stage.Length + 1).Replace('\', '/')] = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()
}
$inventory = [ordered]@{
    schema = 1; product = 'GRID Autosport VR'; version = $Version; channel = $Channel; revision = $revision; builtUtc = $builtUtc
    supportedGame = [ordered]@{ appId = 255220; avxSha256 = 'c88929ba8ed5327799fb6f57dc7889b2f5e8baec47b92832038af913ff54fc48' }
    files = $files
}
[IO.File]::WriteAllText("$stage\package.json", ($inventory | ConvertTo-Json -Depth 4), $utf8)

$notes = Join-Path $output 'ReleaseNotes.txt'
[IO.File]::WriteAllText($notes, "GRID Autosport VR $Version $Channel`r`n`r`n" + $section.Groups['body'].Value.Trim().Replace("`r`n", "`n").Replace("`n", "`r`n") + "`r`n", $utf8)

$zip = Join-Path $output "GRIDAutosportVR-$Version.zip"
Compress-Archive -LiteralPath $stage -DestinationPath $zip
Invoke-Checked 'Installer compilation' { & $InnoCompiler /Q "/DStage=$stage" "/DPackageVersion=$Version" "/DReleaseNotes=$notes" (Join-Path $root 'installer\GRIDAutosportVR.iss') }
$setup = Join-Path $output "GRIDAutosportVR-$Version-Setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "The installer was not written: $setup" }
Get-Item -LiteralPath $setup, $zip | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name
} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii

if ($dirty) { Write-Warning "Test package from uncommitted changes ($revision). Do not publish it." }
Write-Output "Installer: $setup"
Write-Output "Portable ZIP: $zip"
Write-Output "Checksums: $(Join-Path $output 'SHA256SUMS.txt')"
