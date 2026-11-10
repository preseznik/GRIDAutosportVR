param(
    [Parameter(Mandatory)][string] $LauncherAssembly,
    [Parameter(Mandatory)][string] $StockAvx,
    [Parameter(Mandatory)][string] $Bridge,
    [switch] $IsolatedFilesOnly
)
$ErrorActionPreference = 'Stop'
$type = [Reflection.Assembly]::LoadFrom((Resolve-Path $LauncherAssembly).Path).GetType('GRIDAutosportVR.VrPlaySession', $true)
$flags = [Reflection.BindingFlags]'Static, Public, NonPublic'
$stage = $type.GetMethod('Stage', $flags)
$recover = $type.GetMethod($(if ($IsolatedFilesOnly) { 'RestoreLease' } else { 'Recover' }), $flags)
$isOurGame = $type.GetMethod('IsOurGame', $flags)
$activated = $type.GetMethod('Activated', $flags)
$stockHash = 'C88929BA8ED5327799FB6F57DC7889B2F5E8BAEC47B92832038AF913FF54FC48'
$vrHash = '9D3E8F9A36571E20707187F1DA86967201AB74E72F9F72E8EC446F01DBE59856'
$source = (Resolve-Path $StockAvx).Path
$payload = (Resolve-Path $Bridge).Path
if ((Get-FileHash -LiteralPath $source).Hash -ne $stockHash) { throw 'An exact stock AVX fixture is required.' }
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = Join-Path $tempRoot ('gridautosportvr-play-test-' + [guid]::NewGuid().ToString('N'))
$originalLocal = $env:LOCALAPPDATA
$folder = [IO.Path]::GetFullPath([IO.Path]::Combine($root, 'steamapps/common/GRID Autosport'))
$exe = Join-Path $folder 'GRIDAutosport_avx.exe'
$dll = Join-Path $folder 'gasxr.dll'
$lease = Join-Path $folder 'gasvr-play-session.json'
function Check([bool]$condition, [string]$detail) { if (-not $condition) { throw $detail } }
function ExpectFailure([scriptblock]$operation, [string]$detail) {
    try { & $operation } catch { return }
    throw $detail
}
function Stage {
    $token = [guid]::NewGuid().ToString('N')
    [void]$stage.Invoke($null, @($folder, $payload.ToString(), $token))
    Check ((Get-FileHash -LiteralPath $exe).Hash -eq $vrHash) 'candidate hash mismatch'
    Check ((Get-FileHash -LiteralPath $dll).Hash -eq (Get-FileHash -LiteralPath $payload).Hash) 'bridge hash mismatch'
    return $token
}
function CommandLineCheck([string]$arg, [string]$token, [bool]$expected) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($value in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $root 'wait.ps1'), $arg)) { $start.ArgumentList.Add($value) }
    $p = [Diagnostics.Process]::Start($start)
    try {
        $found = $false
        for ($try = 0; $try -lt 15; $try++) {
            $found = $isOurGame.Invoke($null, @($p, $PSHOME, $token))
            if ($found -or $p.HasExited) { break }
            Start-Sleep -Milliseconds 40
        }
        Check ($found -eq $expected) "Process identity mismatch for argument '$arg'"
    } finally { $p.WaitForExit(); $p.Dispose() }
}
try {
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    $env:LOCALAPPDATA = Join-Path $root 'user'
    [IO.File]::WriteAllText((Join-Path $root 'steamapps/appmanifest_255220.acf'), "`"appid`" `"255220`"`n`"installdir`" `"GRID Autosport`"`n")
    [IO.File]::WriteAllBytes((Join-Path $folder 'GRIDAutosport.exe'), [byte[]]@(0))
    [IO.File]::Copy($source, $exe)
    Check (-not $recover.Invoke($null, @($folder))) 'missing lease was treated as active'
    $id = Stage
    $saved = Join-Path $env:LOCALAPPDATA "GRIDAutosportVR/sessions/$id"
    $tokenFile = Join-Path $saved 'session.id'
    $tokenBytes = [IO.File]::ReadAllBytes($tokenFile)
    Check ($tokenBytes.Length -eq 32 -and [Text.Encoding]::ASCII.GetString($tokenBytes) -ceq $id) 'native token encoding mismatch'
    Check ($recover.Invoke($null, @($folder))) 'stale session was not restored'
    Check (-not (Test-Path $tokenFile) -and (Test-Path (Join-Path $saved 'supervisor.log'))) 'completed token was not revoked or evidence was removed'
    Check ((Get-FileHash -LiteralPath $exe).Hash -eq $stockHash -and -not (Test-Path $dll) -and -not (Test-Path $lease)) 'recovery incomplete'
    Check (-not $recover.Invoke($null, @($folder))) 'restoration was not idempotent'
    'PASS: verified staging, exact native token, stale recovery and repeated recovery'

    $id = Stage
    Add-Type -TypeDefinition @'
using System;
using System.Threading.Tasks;
public static class DelayedGameTestFile {
    public static async Task Release(IDisposable held) {
        await Task.Delay(800).ConfigureAwait(false);
        held.Dispose();
    }
}
'@
    $locked = [IO.FileStream]::new($dll, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $release = [DelayedGameTestFile]::Release($locked)
    try { Check ($recover.Invoke($null, @($folder))) 'DLL did not recover after its desktop handle released' }
    finally { [void]$release.GetAwaiter().GetResult(); $locked.Dispose() }
    $log = [IO.File]::ReadAllText((Join-Path $env:LOCALAPPDATA "GRIDAutosportVR/sessions/$id/supervisor.log"))
    Check ($log.Contains('Waiting for Windows') -and $log.Contains('Released staged bridge after') -and -not (Test-Path $dll) -and -not (Test-Path $lease) -and -not (Test-Path (Join-Path $env:LOCALAPPDATA "GRIDAutosportVR/sessions/$id/session.id"))) 'transient no-delete share was not retried or token was not revoked'
    $id = Stage
    $locked = [IO.FileStream]::new($dll, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    try { ExpectFailure { $recover.Invoke($null, @($folder)) } 'locked DLL was removed' }
    finally { $locked.Dispose() }
    Check ($elapsed.Elapsed.TotalSeconds -ge 7.5 -and $elapsed.Elapsed.TotalSeconds -lt 16 -and (Test-Path $lease) -and (Test-Path $dll) -and (Test-Path (Join-Path $env:LOCALAPPDATA "GRIDAutosportVR/sessions/$id/session.id"))) 'persistent lock did not preserve lease and token after bounded retry'
    [IO.File]::SetAttributes($dll, [IO.FileAttributes]::ReadOnly)
    ExpectFailure { $recover.Invoke($null, @($folder)) } 'read-only DLL was changed'
    Check (([IO.File]::GetAttributes($dll) -band [IO.FileAttributes]::ReadOnly) -ne 0 -and (Test-Path $lease)) 'read-only attribute or lease lost'
    [IO.File]::SetAttributes($dll, [IO.FileAttributes]::Normal)
    Check ($recover.Invoke($null, @($folder))) 'released DLL could not be recovered'
    'PASS: delayed no-delete handle retried, persistent handle bounded at eight seconds, read-only DLL preserved'

    [IO.File]::WriteAllText($dll, 'foreign')
    ExpectFailure { Stage } 'pre-existing bridge was overwritten'
    Check ([IO.File]::ReadAllText($dll) -ceq 'foreign') 'pre-existing bridge changed'
    [IO.File]::Delete($dll)
    $id = Stage
    $saved = Join-Path $env:LOCALAPPDATA "GRIDAutosportVR/sessions/$id"
    $tokenFile = Join-Path $saved 'session.id'
    [IO.File]::WriteAllText($tokenFile, 'wrong')
    ExpectFailure { $recover.Invoke($null, @($folder)) } 'foreign token accepted'
    [IO.File]::WriteAllBytes($tokenFile, [Text.Encoding]::ASCII.GetBytes($id))
    $candidate = [IO.File]::ReadAllBytes($exe)
    [IO.File]::WriteAllText($exe, 'foreign-updated-game')
    ExpectFailure { $recover.Invoke($null, @($folder)) } 'changed game was overwritten'
    Check ([IO.File]::ReadAllText($exe) -ceq 'foreign-updated-game') 'changed executable lost'
    [IO.File]::WriteAllBytes($exe, $candidate)
    $backup = Join-Path $saved 'original-avx.exe'
    [IO.File]::WriteAllText($backup, 'bad')
    ExpectFailure { $recover.Invoke($null, @($folder)) } 'bad backup was accepted'
    Check ((Get-FileHash -LiteralPath $exe).Hash -eq $vrHash) 'unverified backup applied'
    [IO.File]::Copy($source, $backup, $true)
    [IO.File]::WriteAllText($dll, 'another bridge')
    ExpectFailure { $recover.Invoke($null, @($folder)) } 'changed bridge was deleted'
    Check ([IO.File]::ReadAllText($dll) -ceq 'another bridge' -and (Get-FileHash -LiteralPath $exe).Hash -eq $stockHash) 'independent bridge or original game lost'
    [IO.File]::Copy($payload, $dll, $true)
    Check ($recover.Invoke($null, @($folder))) 'repair retry failed'
    'PASS: pre-existing bridge, changed game, invalid token, damaged backup and independent DLL preserved'

    $id = Stage
    $saved = Join-Path $env:LOCALAPPDATA "GRIDAutosportVR/sessions/$id"
    $prefix = Join-Path $folder "gasvr-play-$id"
    foreach ($kind in @('.exe.tmp', '.dll.tmp', '.restore.tmp')) { [IO.File]::WriteAllText(($prefix + $kind), 'partial') }
    $requested = [DateTime]::UtcNow
    Check (-not $activated.Invoke($null, @($id, $requested))) 'missing renderer log accepted'
    $runtimeLog = Join-Path $saved 'bridge.log'
    [IO.File]::WriteAllText($runtimeLog, 'D3D11 started')
    Check (-not $activated.Invoke($null, @($id, $requested))) 'incomplete renderer activation accepted'
    [IO.File]::AppendAllText($runtimeLog, "`n[42] native renderer configured: 800x600")
    Check ($activated.Invoke($null, @($id, $requested))) 'native renderer receipt not recognized'
    Check (-not $activated.Invoke($null, @($id, $requested.AddMinutes(2)))) 'stale renderer receipt accepted'
    Check ($recover.Invoke($null, @($folder))) 'interrupted nonce-scoped temp files blocked stock restoration'
    Check ((Get-FileHash -LiteralPath $exe).Hash -eq $stockHash -and -not (Test-Path $dll) -and -not (Test-Path $lease)) 'incomplete staged file contaminated recovery'
    foreach ($kind in @('.exe.tmp', '.dll.tmp', '.restore.tmp')) { Check ([IO.File]::ReadAllText(($prefix + $kind)) -ceq 'partial') 'unverified temp was overwritten' }
    'PASS: interrupted stage and restore files preserved without blocking recovery, missing/stale/valid native receipts'

    $id = Stage
    $other = Join-Path $folder 'gasvr-benchmark-session.json'
    [IO.File]::WriteAllText($other, '{}')
    ExpectFailure { $recover.Invoke($null, @($folder)) } 'benchmark lease was ignored'
    Check ((Get-FileHash -LiteralPath $exe).Hash -eq $vrHash) 'competing lease touched installation'
    [IO.File]::Delete($other)
    Check ($recover.Invoke($null, @($folder))) 'regular session did not recover after removing foreign lease'
    [IO.File]::WriteAllText((Join-Path $root 'wait.ps1'), 'Start-Sleep -Seconds 1')
    CommandLineCheck "-gasvrplay=$id" $id $true
    CommandLineCheck "--unrelated=embedded -gasvrplay=$id tail" $id $false
    CommandLineCheck "-gasvrplay=$id" ([guid]::NewGuid().ToString('N')) $false
    'PASS: competing benchmark lease, exact native process argument, embedded text and foreign token'
} finally {
    $env:LOCALAPPDATA = $originalLocal
    $resolved = [IO.Path]::GetFullPath($root)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::TrimEndingDirectorySeparator($tempRoot) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^gridautosportvr-play-test-[0-9a-f]{32}$') { throw 'Invalid test cleanup path' }
    if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
