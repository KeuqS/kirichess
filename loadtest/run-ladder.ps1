param(
    [Parameter(Mandatory = $true)][string]$LoadCpus,
    [Parameter(Mandatory = $true)][string]$LoadMemory,
    [Parameter(Mandatory = $true)][int[]]$Games,
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$Pool = "steal3",
    [string]$Api = "http://localhost:8080",
    [string]$ScrapeToken = "load-only-not-a-secret-scrape-token",
    [int]$ThinkMs = 3000,
    [int]$JitterMs = 1000,
    [int]$WarmupSeconds = 90,
    [int]$WindowSeconds = 180,
    [string]$Presence = "true",
    [int]$Seed = 20260808,
    [string]$LagCompensation = "true",
    [double]$PinnedCpus = 1.0,
    [string]$OutDir = "loadtest/results",
    [switch]$StopOnFail = $true
)

# Runs Phase 9b.3's ladder for one (budget, list-of-N) pair. A fresh
# stack per rung is not optional (task file § 2.6): `down -v` after every rung guarantees the next N
# starts from an empty database, so a degraded persister backlog or 2N dead accounts from one rung can
# never contaminate the next.
#
# PowerShell's Set-Content/Get-Content double-encode this repo's em-dashes, so this file (and every
# other file in this phase) was written with the Write tool, never piped through PowerShell.

$ErrorActionPreference = "Stop"
$composeArgs = @("-p", "kirichess-load", "-f", "docker-compose.prod.yml", "-f", "docker-compose.load.yml")

Write-Host "=== Ladder: label=$Label cpus=$LoadCpus memory=$LoadMemory lagCompensation=$LagCompensation games=$($Games -join ',') ==="

foreach ($n in $Games) {
    $env:LOAD_CPUS = $LoadCpus
    $env:LOAD_MEMORY = $LoadMemory
    $env:LOAD_LAG_COMPENSATION = $LagCompensation

    Write-Host ""
    Write-Host "--- Rung N=$n ---"
    Write-Host "Bringing up db redis backend..."
    docker compose @composeArgs up -d db redis backend
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed for N=$n" }

    Write-Host "Verifying deploy.resources.limits applied..."
    docker inspect kirichess-backend --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}}'

    Write-Host "Running migrations..."
    docker compose @composeArgs --profile migrate run --rm migrate
    if ($LASTEXITCODE -ne 0) { throw "migrate failed for N=$n" }

    $rungLabel = "$Label-n$n"
    $out = Join-Path $OutDir "$rungLabel.json"

    Write-Host "Running harness: N=$n label=$rungLabel out=$out"
    dotnet run -c Release --project loadtest/KiriChess.LoadTest -- `
        --games $n --pool $Pool --api $Api --scrape-token $ScrapeToken `
        --think-ms $ThinkMs --jitter-ms $JitterMs `
        --warmup-seconds $WarmupSeconds --window-seconds $WindowSeconds `
        --presence $Presence --seed $Seed --pinned-cpus $PinnedCpus --load-memory $LoadMemory `
        --label $rungLabel --out $out
    $harnessExit = $LASTEXITCODE

    Write-Host "Tearing down (down -v)..."
    docker compose @composeArgs down -v

    if ($harnessExit -ne 0) {
        Write-Host "Rung N=$n did not pass every criterion (exit $harnessExit). See $out."
        if ($StopOnFail) {
            Write-Host "Stopping ladder on first failing/void rung."
            break
        }
    }
    else {
        Write-Host "Rung N=$n passed."
    }
}

Write-Host ""
Write-Host "=== Ladder finished ==="
