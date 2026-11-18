param([Parameter(Mandatory)][string]$Sampler)

# Runs the sampler the way Orbit does (--feed tied to a parent process) and checks that it keeps live.json and
# history current in the OS data folder, then exits once the parent is gone.
$ErrorActionPreference = 'Stop'
$dataDir = if ($IsWindows) { Join-Path $env:APPDATA 'PerfWidget' }
           elseif ($IsMacOS) { Join-Path $HOME 'Library/Application Support/PerfWidget' }
           else { Join-Path ($env:XDG_DATA_HOME ? $env:XDG_DATA_HOME : (Join-Path $HOME '.local/share')) 'PerfWidget' }
$feed = Join-Path $dataDir 'live.json'
$fail = [System.Collections.Generic.List[string]]::new()
function Check([bool]$ok, [string]$msg) { if (-not $ok) { $fail.Add($msg) } else { Write-Host "ok   $msg" } }

$parent = Start-Process pwsh -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 600' -PassThru
$s = Start-Process $Sampler -ArgumentList '--feed', '--parent-pid', $parent.Id -PassThru
Start-Sleep 14

Check (Test-Path $feed) "feed written to $feed"
if (Test-Path $feed) {
    $j = Get-Content $feed -Raw | ConvertFrom-Json
    $age = ((Get-Date).ToUniversalTime() - ([datetime]$j.t).ToUniversalTime()).TotalSeconds
    Check ($j.writer -eq 'sampler') "feed writer is the sampler (got '$($j.writer)')"
    Check ($j.pid -eq $s.Id) "feed pid is the sampler's ($($j.pid) vs $($s.Id))"
    Check ($age -lt 5) "feed is fresh ($([math]::Round($age,1)) s old)"
    Check (@($j.apps).Count -ge 5) "feed lists apps ($(@($j.apps).Count))"
}
$hist = @(Get-ChildItem (Join-Path $dataDir 'history') -Filter '*.jsonl' -ErrorAction SilentlyContinue)
Check ($hist.Count -ge 1 -and $hist[0].Length -gt 0) "history recorded ($($hist.Count) file(s))"

Stop-Process -Id $parent.Id -Force
Start-Sleep 5
$still = Get-Process -Id $s.Id -ErrorAction SilentlyContinue
Check (-not $still -or $still.HasExited) "sampler exits once its parent is gone"
if ($still -and -not $still.HasExited) { Stop-Process -Id $s.Id -Force }

if ($fail.Count -gt 0) { foreach ($f in $fail) { Write-Host "FAIL $f" -ForegroundColor Red }; exit 1 }
Write-Host "`nfeed checks passed"
