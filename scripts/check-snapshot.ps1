param([Parameter(Mandatory)][string]$Path, [string]$Os = "")

# Sanity checks on a live.json-format snapshot taken while a known single-core hog is running.
# Fails loudly with every problem found, not just the first.
if (-not (Test-Path $Path)) { Write-Host "FAIL snapshot file $Path was not written" -ForegroundColor Red; exit 1 }
$j = Get-Content $Path -Raw | ConvertFrom-Json
$fail = [System.Collections.Generic.List[string]]::new()
function Check([bool]$ok, [string]$msg) { if (-not $ok) { $fail.Add($msg) } else { Write-Host "ok   $msg" } }

$expected = @{ Windows = 'windows'; macOS = 'macos'; Linux = 'linux' }[$Os]
if ($expected) { Check ($j.platform -eq $expected) "platform is $expected (got '$($j.platform)')" }

Check ($j.v -eq 1) "schema v1"
Check ($j.cpu.logical -ge 1) "logical cpus >= 1 ($($j.cpu.logical))"
Check ($j.cpu.processes -ge 20) "process count >= 20 ($($j.cpu.processes))"
Check ($j.cpu.threads -ge $j.cpu.processes) "threads >= processes ($($j.cpu.threads))"
# One spinning thread out of N logical CPUs is at least (100/N)% of the machine.
$floor = [math]::Max(1, 100 / $j.cpu.logical * 0.5)
Check ($j.cpu.pct -ge $floor -and $j.cpu.pct -le 100) "cpu pct plausible with a hog running ($($j.cpu.pct)% >= $floor%)"

Check ($j.memory.totalBytes -gt 1GB) "ram total > 1 GB ($([math]::Round($j.memory.totalBytes/1GB,1)) GB)"
Check ($j.memory.usedBytes -gt 100MB -and $j.memory.usedBytes -lt $j.memory.totalBytes) "ram used in (100 MB, total) ($([math]::Round($j.memory.usedBytes/1GB,2)) GB)"
Check ($j.memory.pct -gt 0 -and $j.memory.pct -lt 100) "ram pct in (0,100) ($($j.memory.pct))"

Check (@($j.drives).Count -ge 1) "at least one drive ($(@($j.drives | ForEach-Object name) -join ', '))"
Check (@($j.drives | Where-Object { $_.totalBytes -gt 1GB -and $_.freeBytes -gt 0 -and $_.freeBytes -le $_.totalBytes }).Count -ge 1) "a drive with sane free/total"
Check (@($j.disks).Count -ge 1) "at least one physical disk ($(@($j.disks | ForEach-Object name) -join ', '))"

$apps = @($j.apps)
Check ($apps.Count -ge 5) "at least 5 apps ($($apps.Count))"
Check (@($apps | Where-Object { $_.ramBytes -gt 0 }).Count -ge 3) "apps have RAM numbers"
$hog = $apps | Sort-Object cpuPct -Descending | Select-Object -First 1
Write-Host "busiest app: $($hog.name)  cpu $($hog.cpuPct)%  thread $($hog.maxThreadPct)%  ram $([math]::Round($hog.ramBytes/1MB)) MB"
Check ($hog.cpuPct -ge $floor) "busiest app uses at least one core's share ($($hog.name) $($hog.cpuPct)%)"
$pinned = $apps | Where-Object { $_.maxThreadPct -ge 70 }
Check (@($pinned).Count -ge 1) "single-thread detection sees the hog (maxThreadPct >= 70: $(@($pinned | ForEach-Object { "$($_.name) $($_.maxThreadPct)%" }) -join ', '))"
$py = $apps | Where-Object { $_.ramBytes -ge 250MB -and $_.maxThreadPct -ge 70 }
Check (@($py).Count -ge 1) "one app has both the hog's pinned thread and its 300 MB ($(@($py | ForEach-Object name) -join ', '))"

Write-Host ""
Write-Host "verdict: [$($j.verdict.severity)] $($j.verdict.title) $($j.verdict.detail)"
foreach ($i in $j.issues) { Write-Host "  issue [$($i.severity)] $($i.title): $($i.detail)" }

if ($fail.Count -gt 0) {
    Write-Host ""
    foreach ($f in $fail) { Write-Host "FAIL $f" -ForegroundColor Red }
    exit 1
}
Write-Host "`nall checks passed"
