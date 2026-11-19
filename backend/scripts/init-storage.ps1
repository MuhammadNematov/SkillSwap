# Первичная настройка локального хранилища файлов (Garage): раскладка кластера, ключ доступа приложения и бакет.
# Запуск (из папки backend, после `docker compose up -d garage`):  ./scripts/init-storage.ps1
# Скрипт можно запускать повторно: уже сделанное он пропускает. Значения берутся из backend/.env
# (STORAGE_ACCESS_KEY, STORAGE_SECRET_KEY), бакет по умолчанию называется skillswap.
param(
    [string]$Bucket = "skillswap",
    [string]$Container = "skillswap-garage"
)

$ErrorActionPreference = "Stop"

function Read-EnvValue([string]$name) {
    $line = Get-Content (Join-Path $PSScriptRoot "../.env") | Where-Object { $_ -match "^\s*$name\s*=" } | Select-Object -First 1
    if (-not $line) { throw "В backend/.env нет $name (шаблон: backend/.env.example)." }
    return ($line -split "=", 2)[1].Trim()
}

$accessKey = Read-EnvValue "STORAGE_ACCESS_KEY"
$secretKey = Read-EnvValue "STORAGE_SECRET_KEY"
if ($accessKey -notmatch '^GK[0-9a-f]{24}$') { throw "STORAGE_ACCESS_KEY: 'GK' и 24 шестнадцатеричных символа (строчные)." }
if ($secretKey -notmatch '^[0-9a-f]{64}$') { throw "STORAGE_SECRET_KEY: 64 шестнадцатеричных символа (строчные)." }

function Garage { & docker exec $Container /garage @args; if ($LASTEXITCODE -ne 0) { throw "garage $($args -join ' ') завершилась с ошибкой" } }
function GarageTry {
    # Garage пишет журнал в stderr: при Stop это считалось бы ошибкой, поэтому проверяем только код выхода.
    $ErrorActionPreference = "Continue"
    & docker exec $Container /garage @args 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}

# 1. Раскладка: один узел, зона dc1, 1 ГБ (для разработки достаточно; настоящий объём задаётся на сервере).
$status = (& docker exec $Container /garage status) -join "`n"
if ($status -match "NO ROLE ASSIGNED") {
    $nodeId = ($status -split "`n" | Where-Object { $_ -match '^[0-9a-f]{16}\s' } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
    Garage layout assign -z dc1 -c 1G $nodeId
    Garage layout apply --version 1
    Start-Sleep -Seconds 2
    Write-Host "Раскладка применена."
} else {
    Write-Host "Раскладка уже есть."
}

# 2. Ключ доступа приложения (создаётся с заданными значениями) и бакет.
if (GarageTry key import --yes $accessKey $secretKey -n skillswap-app) { Write-Host "Ключ создан." } else { Write-Host "Ключ уже есть." }
if (GarageTry bucket create $Bucket) { Write-Host "Бакет $Bucket создан." } else { Write-Host "Бакет $Bucket уже есть." }
Garage bucket allow --read --write --owner $Bucket --key skillswap-app

Write-Host ""
Write-Host "Готово. Один раз задайте приложению (user-secrets, из папки backend/src/SkillSwap.Api):"
Write-Host "  dotnet user-secrets set `"Storage:Endpoint`" `"http://localhost:3900`""
Write-Host "  dotnet user-secrets set `"Storage:Bucket`" `"$Bucket`""
Write-Host "  dotnet user-secrets set `"Storage:AccessKey`" `"$accessKey`""
Write-Host "  dotnet user-secrets set `"Storage:SecretKey`" `"$secretKey`""
