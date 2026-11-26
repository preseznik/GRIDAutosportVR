<#
.SYNOPSIS
Stages the career reader's emulator: unicorn.dll from the pinned Unicorn 2.1.4 win_amd64 wheel.

.DESCRIPTION
The launcher reads career details (Profile tab) by running the game's own save codec from the installed
GRIDAutosport_avx.exe under Unicorn (launcher/CareerSaves.cs). This downloads the pinned wheel and its
source distribution once into .deps\career-unicorn (checked by SHA-256, as GRID2VR does), writes
unicorn.dll to -Destination and, with -Notices, the GPLv2 source and a README for licenses\Unicorn.
#>
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$Notices
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root '.deps\career-unicorn'
$wheel = @{ Path = '70/3d/de7be9bd1addabe6d8a1369381f8a080400c349850e978689c5e18287957/unicorn-2.1.4-cp37-abi3-win_amd64.whl'
            Sha = 'd7107500c64ce5c168fbff6bef9485b5db1350050036f4cea568650cf8bdbdf5' }
$source = @{ Path = 'b2/1b/b4248aa8422e86de690cf8e85cf8feae4c33405a097d1ebe71570bdaa6f5/unicorn-2.1.4.tar.gz'
             Sha = '00567a70e323f749b419cd86bee4f9115beab7ebba32194581c090cbb7c59cff' }
$dllSha = '010d85dce84b8f6ffaec3d5420f1b5f7768f859eb831be574c7da23e656ee8f1'  # launcher/CareerSaves.cs UnicornSha256

function Get-Distribution($item) {
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $file = Join-Path $cache (Split-Path -Leaf $item.Path)
    if ((Test-Path -LiteralPath $file) -and (Get-FileHash -LiteralPath $file).Hash -eq $item.Sha) { return $file }
    $download = "$file.download"
    Invoke-WebRequest -Uri ('https://files.pythonhosted.org/packages/' + $item.Path) -OutFile $download -TimeoutSec 120
    if ((Get-FileHash -LiteralPath $download).Hash -ne $item.Sha) { Remove-Item -LiteralPath $download; throw "Unexpected SHA-256 for $(Split-Path -Leaf $file)" }
    Move-Item -LiteralPath $download -Destination $file -Force
    return $file
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Get-Distribution $wheel))
try {
    $entry = $zip.GetEntry('unicorn/lib/unicorn.dll')
    if (-not $entry) { throw 'The Unicorn wheel has no unicorn/lib/unicorn.dll.' }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $target = Join-Path $Destination 'unicorn.dll'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, "$target.tmp", $true)
} finally { $zip.Dispose() }
if ((Get-FileHash -LiteralPath "$target.tmp").Hash -ne $dllSha) { Remove-Item -LiteralPath "$target.tmp"; throw 'Unexpected unicorn.dll in the pinned wheel.' }
Move-Item -LiteralPath "$target.tmp" -Destination $target -Force

if ($Notices) {
    New-Item -ItemType Directory -Path $Notices -Force | Out-Null
    Copy-Item -LiteralPath (Get-Distribution $source) -Destination $Notices
    [IO.File]::WriteAllText((Join-Path $Notices 'README.txt'),
        "career\unicorn.dll is Unicorn Engine 2.1.4 (GPLv2), used by the launcher to read career details.`r`n" +
        "The accompanying source distribution includes build files and license notices.`r`n" +
        "https://github.com/unicorn-engine/unicorn/tree/2.1.4`r`n", (New-Object Text.UTF8Encoding($false)))
}
Write-Output $target
