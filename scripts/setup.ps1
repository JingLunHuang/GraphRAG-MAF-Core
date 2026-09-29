param([switch]$Start)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $workspace
try {
    if (-not (Test-Path -LiteralPath '.env')) {
        $bytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try {
            $rng.GetBytes($bytes)
            $neoPassword = ([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant()
            $rng.GetBytes($bytes)
            $apiKey = ([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant()
        } finally { $rng.Dispose() }
        $content = (Get-Content -LiteralPath '.env.example' -Raw).Replace("NEO4J_PASSWORD=", "NEO4J_PASSWORD=$neoPassword").Replace("APP_API_KEY=", "APP_API_KEY=$apiKey")
        [IO.File]::WriteAllText((Join-Path $workspace '.env'), $content, (New-Object Text.UTF8Encoding $false))
        Write-Host 'Created .env with generated local secrets. Secrets are not printed.'
    }
    if ($Start) {
        $dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
        $dockerExe = if ($dockerCommand) { $dockerCommand.Source } else { 'C:\Program Files\Docker\Docker\resources\bin\docker.exe' }
        if (-not (Test-Path -LiteralPath $dockerExe)) { throw 'Docker Desktop or Docker Engine with Compose is required.' }
        & $dockerExe compose up --build -d --wait
        if ($LASTEXITCODE -ne 0) { throw 'Docker Compose deployment failed.' }
        Write-Host 'API: http://localhost:8080 | Jaeger: http://localhost:16686 | Prometheus: http://localhost:9090'
    }
} finally { Pop-Location }
