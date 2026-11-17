# Бэкенд: локальный запуск

Нужны: .NET 10 SDK, Docker.

## 1. База данных

```
cd backend
copy .env.example .env        # и задайте POSTGRES_PASSWORD
docker compose up -d
```

Postgres слушает порт **5433** (не 5432: на машине может быть свой PostgreSQL).

## 2. Секреты (user-secrets, не в репозитории)

```
cd backend/src/SkillSwap.Api
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5433;Database=skillswap;Username=skillswap;Password=<пароль из .env>"
dotnet user-secrets set "Jwt:SigningKey" "<случайная строка не короче 32 символов>"
```

В CI и на сервере те же настройки задаются переменными окружения: `ConnectionStrings__Default`, `Jwt__SigningKey`.
Без ключа JWT приложение не запускается.

## 3. Миграции

```
cd backend
dotnet ef database update --project src/SkillSwap.Infrastructure --startup-project src/SkillSwap.Api
```

## 4. Суперадмин (необязательно)

Первый суперадмин создаётся при старте приложения из двух переменных (раздел 3 ТЗ):

```
Superadmin__Email=root@example.com
Superadmin__Password=<пароль, минимум 8 символов и цифра>
```

- Нужны обе переменные сразу, иначе приложение не запустится.
- Если суперадмин уже есть, ничего не происходит (пароль из окружения его не перезаписывает).
- Аккаунт создаётся без 2FA. При первом входе `POST /api/v1/auth/login` вернёт `requires2faSetup: true` и `twoFactorToken`:
  дальше `POST /auth/2fa/setup` и `POST /auth/2fa/verify` с этим токеном в теле (первый код из приложения-аутентификатора),
  и вход завершится сразу. Дальше вход только в два шага (`/auth/login` → `/auth/login/2fa`).
- Отключить 2FA у служебного аккаунта нельзя.

Локально проще всего задать их в user-secrets: `dotnet user-secrets set "Superadmin:Email" "..."`.

## 5. Запуск и документация API

```
cd backend
dotnet run --project src/SkillSwap.Api
```

Документация (Scalar) открывается только в Development: `/scalar`. Bearer-токен вставляется в окне авторизации.

## Тесты

```
cd backend
dotnet test
```

Интеграционные тесты сами поднимают PostgreSQL в Docker (Testcontainers), Docker должен быть запущен.
