#!/usr/bin/env bash
# Первичная настройка локального хранилища файлов (Garage): раскладка кластера, ключ доступа приложения и бакет.
# Запуск (из папки backend, после `docker compose up -d garage`):  ./scripts/init-storage.sh
# Можно запускать повторно: уже сделанное пропускается. Значения берутся из backend/.env.
set -euo pipefail

BUCKET="${1:-skillswap}"
CONTAINER="${2:-skillswap-garage}"
ENV_FILE="$(dirname "$0")/../.env"

read_env() {
  local line
  line="$(grep -E "^[[:space:]]*$1[[:space:]]*=" "$ENV_FILE" | head -n1 || true)"
  [ -n "$line" ] || { echo "В backend/.env нет $1 (шаблон: backend/.env.example)." >&2; exit 1; }
  echo "${line#*=}" | tr -d '[:space:]'
}

ACCESS_KEY="$(read_env STORAGE_ACCESS_KEY)"
SECRET_KEY="$(read_env STORAGE_SECRET_KEY)"
[[ "$ACCESS_KEY" =~ ^GK[0-9a-f]{24}$ ]] || { echo "STORAGE_ACCESS_KEY: 'GK' и 24 шестнадцатеричных символа (строчные)." >&2; exit 1; }
[[ "$SECRET_KEY" =~ ^[0-9a-f]{64}$ ]] || { echo "STORAGE_SECRET_KEY: 64 шестнадцатеричных символа (строчные)." >&2; exit 1; }

garage() { docker exec "$CONTAINER" /garage "$@"; }

STATUS="$(garage status)"
if grep -q "NO ROLE ASSIGNED" <<<"$STATUS"; then
  NODE_ID="$(grep -E '^[0-9a-f]{16}[[:space:]]' <<<"$STATUS" | head -n1 | awk '{print $1}')"
  garage layout assign -z dc1 -c 1G "$NODE_ID"
  garage layout apply --version 1
  sleep 2
  echo "Раскладка применена."
else
  echo "Раскладка уже есть."
fi

if garage key import --yes "$ACCESS_KEY" "$SECRET_KEY" -n skillswap-app >/dev/null 2>&1; then echo "Ключ создан."; else echo "Ключ уже есть."; fi
if garage bucket create "$BUCKET" >/dev/null 2>&1; then echo "Бакет $BUCKET создан."; else echo "Бакет $BUCKET уже есть."; fi
garage bucket allow --read --write --owner "$BUCKET" --key skillswap-app

echo
echo "Готово. Один раз задайте приложению (user-secrets, из папки backend/src/SkillSwap.Api):"
echo "  dotnet user-secrets set \"Storage:Endpoint\" \"http://localhost:3900\""
echo "  dotnet user-secrets set \"Storage:Bucket\" \"$BUCKET\""
echo "  dotnet user-secrets set \"Storage:AccessKey\" \"$ACCESS_KEY\""
echo "  dotnet user-secrets set \"Storage:SecretKey\" \"$SECRET_KEY\""
