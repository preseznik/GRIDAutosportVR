# Development-only, read-only export of every car's stock cockpit atlas (<car>_cabin.tga in
# cars/models/<car>/livery_user/textures_low/<car>_tex_low_user.pssg: 512x512 DXT5, 9 mips),
# the source for redrawn cockpit textures as in GRID2VR (tools/Export-CockpitTextures.ps1 there).
# Never writes into the game folder.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Library = 'F:/Coding/Codex/Mods/DiRT2VR/artifacts/xml-convert/EgoEngineLibrary.dll'
)
$ErrorActionPreference = 'Stop'
$game = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($output -eq $game -or $output.StartsWith($game + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Export outside the installed game.' }
if ((Get-FileHash -LiteralPath $Library).Hash.ToLowerInvariant() -ne 'b27a45447cd08a8e46423f16151a9d375a179a374ab83475b8029749ff528d98') { throw 'Unexpected EgoEngineLibrary.dll.' }
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory.' }
Add-Type -Path $Library
[IO.Directory]::CreateDirectory($output) | Out-Null
function Walk-Texture($node) { if ($node.Name -eq 'TEXTURE') { $node }; foreach ($child in $node.ChildElements) { Walk-Texture $child } }
$rows = [Collections.Generic.List[object]]::new()
foreach ($folder in Get-ChildItem -LiteralPath (Join-Path $game 'cars/models') -Directory | Sort-Object Name) {
    $car = $folder.Name
    if ($car -cnotmatch '^[a-z0-9]{3}$') { throw "Unexpected car folder $car" }
    $origin = "cars/models/$car/livery_user/textures_low/${car}_tex_low_user.pssg"
    $path = Join-Path $game $origin
    $before = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
    $stream = [IO.File]::OpenRead($path)
    try { $pssg = [EgoEngineLibrary.Graphics.Pssg.PssgFile]::Open($stream) } finally { $stream.Dispose() }
    $textures = @(Walk-Texture $pssg.RootElement | Where-Object { ($_.Attributes | Where-Object Name -eq 'id').Value -match '_cabin\.tga$' })
    if ($textures.Count -ne 1) { throw "Expected one cabin atlas for $car, found $($textures.Count)" }
    $target = Join-Path $output "$car.dds"
    $stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { [EgoEngineLibrary.Graphics.GraphicsExtensions]::ToDdsFile($textures[0]).Write($stream) } finally { $stream.Dispose() }
    if ((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $before) { throw 'Game source changed during export' }
    $attributes = [ordered]@{}; foreach ($a in $textures[0].Attributes) { $attributes[$a.Name] = $a.Value }
    $rows.Add([ordered]@{ car = $car; origin = $origin; sourceSha256 = $before; texture = $attributes; ddsSha256 = (Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() })
}
[ordered]@{ schema = 1; readOnly = $true; cars = $rows.ToArray() } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'inventory.json') -Encoding utf8
Write-Output "Exported $($rows.Count) cockpit atlases read-only: $output"
