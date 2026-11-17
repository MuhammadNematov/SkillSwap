# Бэкенд: локальный запуск

Нужны: .NET 10 SDK, Docker.

## 1. База данных

```
cd backend
copy .env.example .env        # и задайте POSTGRES_PASSWORD и LIVEKIT_API_SECRET
docker compose up -d postgres
```

Postgres слушает порт **5433** (не 5432: на машине может быть свой PostgreSQL).

## 2. Секреты (user-secrets, не в репозитории)

```
cd backend/src/SkillSwap.Api
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5433;Database=skillswap;Username=skillswap;Password=<пароль из .env>"
dotnet user-secrets set "Jwt:SigningKey" "<случайная строка не короче 32 символов>"
dotnet user-secrets set "Turnstile:SecretKey" "1x0000000000000000000000000000000AA"
dotnet user-secrets set "LiveKit:ApiSecret" "<то же значение, что LIVEKIT_API_SECRET в .env>"   # нужно только для LiveKit
```

В CI и на сервере те же настройки задаются переменными окружения: `ConnectionStrings__Default`, `Jwt__SigningKey`, `Turnstile__SecretKey`, `LiveKit__ApiSecret`.
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

## Ограничение частоты запросов

Встроенный в .NET Rate Limiter, счётчики в памяти процесса. Настройки в `backend/src/SkillSwap.Api/appsettings.json`, секция `RateLimiting`:

| Что ограничено | Ключ | По умолчанию |
| --- | --- | --- |
| `forgot-password` и `resend-confirmation`, на один email | `MailPerEmail` | 3 в час |
| те же эндпоинты, на один IP | `MailPerIp` | 10 в час |
| `login`, `login/2fa`, `2fa/setup` и `2fa/verify` вместе, на один IP | `LoginPerIp` | 10 в минуту |
| `register`, на один IP | `RegisterPerIp` | 5 в час |
| `reset-password`, на один IP | `ResetPasswordPerIp` | 10 в час |
| `confirm-email`, на один IP | `ConfirmEmailPerIp` | 20 в час |
| `refresh`, на один IP | `RefreshPerIp` | 30 в минуту |
| остальные запросы авторизованного пользователя, на пользователя | `AuthenticatedPerUser` | 100 в минуту |

Каждое правило: `PermitLimit` (сколько запросов) и `WindowSeconds` (за сколько секунд). Любое значение можно переопределить
переменной окружения, например `RateLimiting__LoginPerIp__PermitLimit=20`. `RateLimiting__Enabled=false` выключает лимиты целиком
(только для нагрузочного теста). Нулевые и отрицательные значения не принимаются: приложение не запустится.

При превышении: **429** с `ProblemDetails` (`code: "rate_limited"`, `retryAfterSeconds`) и заголовком `Retry-After` (секунды).
Окно скользящее, `Retry-After` точный: через столько секунд освободится ближайшая попытка.

Важно:
- Счёт по IP корректен только если приложение видит настоящий адрес клиента. За прокси нужно настроить `ForwardedHeaders` с перечнем доверенных прокси (см. `docs/DEFERRED.md`).
- IPv6-адреса считаются по префиксу /64 (одна подсеть = один клиент).
- Лимит по email считается по тексту адреса (без учёта регистра и пробелов по краям), а не по факту регистрации: поэтому он не выдаёт, кто зарегистрирован.

## Капча (Cloudflare Turnstile)

Капча проверяется на **`POST /auth/register`**, **`POST /auth/login`** и **`POST /auth/forgot-password`** (FR-5).

**Контракт для фронтенда.** Виджет Turnstile выдаёт токен; его нужно передать в теле запроса в поле `captchaToken`:

```json
{ "email": "a@b.com", "password": "...", "captchaToken": "<токен из виджета>" }
```

Токен одноразовый: после каждого запроса (даже неудачного) виджет нужно сбросить и получить новый.

| Ответ | `code` | Когда |
| --- | --- | --- |
| 400 | `captcha_required` | поля нет или оно пустое |
| 400 | `captcha_invalid` | токен не принят: неверный, просроченный, уже использованный |
| 503 | `captcha_unavailable` | Cloudflare не ответил или ответил ошибкой; запрос **не** пропускается |

**Порядок защит:** лимит по IP → капча → остальная логика (валидация, поиск пользователя, пароль, письма) → для forgot-password лимит по email.
Капча идёт до всего остального, поэтому без неё нельзя перебирать пароли и выяснять, какие email зарегистрированы.

**Ключи.** Для разработки у Cloudflare есть публичные тестовые ключи:
- ключ сайта для виджета на фронтенде: `1x00000000000000000000AA` (виджет всегда проходит);
- секрет для бэкенда: `1x0000000000000000000000000000000AA` (всегда пускает); `2x0000000000000000000000000000000AA` — всегда отказывает (проверить ветку ошибки); `3x…AA` — «токен уже использован».

Настоящие ключи берутся в панели Cloudflare. **Секрет не хранится в репозитории**: `Turnstile:SecretKey` только в user-secrets или `Turnstile__SecretKey`, `LiveKit__ApiSecret`. Вне Development тестовый секрет приложение не принимает и не запускается.

Настройки (секция `Turnstile`): `Enabled` (по умолчанию `true`; `false` отключает проверку, только для отладки), `TimeoutSeconds` (ожидание Cloudflare, по умолчанию 5),
`SendClientIp` (передавать ли Cloudflare IP клиента, по умолчанию `false`: IP — персональные данные), `VerifyUrl`.

## LiveKit: прототип связки (этап 2)

Маленькая проверка «токен + подключение + webhook». Настоящая видеокомната (выдача токена только участникам сессии и только в её окне,
pre-join, таймер, автофиксация урока) — этап 5. Сущности `Session` пока нет.

**Запуск.**

1. В `backend/.env` задайте `LIVEKIT_API_SECRET`: случайная строка **от 32 символов** (в `.env.example` есть шаблон). Это секрет ключа `devkey` локального LiveKit.
2. То же значение задайте приложению: `dotnet user-secrets set "LiveKit:ApiSecret" "<значение>" --project src/SkillSwap.Api`.
3. Поднимите медиасервер: `docker compose up -d livekit` (образ `livekit/livekit-server:v1.13.11`, режим `--dev`, настройки в `backend/livekit.yaml`).
4. Запустите API в профиле **http**: `dotnet run --project src/SkillSwap.Api --launch-profile http` (порт **5023**: на него в `livekit.yaml` указан webhook).

**Звонок в двух вкладках.** Откройте `http://localhost:5023/dev/livekit` в двух вкладках, нажмите «Войти» в обеих (имена разные, комната одна), разрешите камеру и микрофон.
Вы должны увидеть и услышать друг друга; кнопка «Пинг» передаёт сообщение между вкладками (если камеры нет, снимите галочки: связь всё равно проверяется).
Параметры адреса: `?room=dev-моя-комната&name=Аня&cam=0&mic=0&auto=1` (`auto=1` входит сразу).

**Что есть.**

| Эндпоинт | Где доступен | Что делает |
| --- | --- | --- |
| `GET /dev/livekit` | **только Development** | тестовая страница звонка |
| `GET /dev/livekit/token?room=&identity=&name=` | **только Development** | выдаёт токен на 5 минут в тестовую комнату (имя комнаты начинается с `dev-`; комнаты `session-…` прототип не выдаёт) |
| `POST /api/v1/webhooks/livekit` | все среды, по подписи | принимает события LiveKit и пишет их в `LiveKitWebhookEvents` |

В остальных средах (не Development) первых двух маршрутов нет вообще (404). Они не входят в OpenAPI/Scalar.

**Webhook.** LiveKit подписывает запрос: в заголовке `Authorization` лежит голый JWT (без `Bearer`), HS256, `iss` = ключ API, `exp` = `nbf` + 5 минут, claim `sha256` = base64 хеша тела.
API проверяет и подпись, и хеш тела, и срок; без секрета в настройках не принимает ничего. Ответы: **200** (записано или уже было), **401** (подпись, всегда один и тот же ответ), **400** (подпись верна, тело не событие LiveKit), **413** (тело больше 256 КБ).
Каждое событие записывается один раз по уникальному `EventId` (повторная доставка отвечает 200 и ничего не меняет, в том числе при гонке). Сырое тело не хранится и не логируется: в нём есть пароль TURN комнаты.
Проверить, что события дошли:

```
docker exec skillswap-postgres psql -U skillswap -d skillswap -c "SELECT \"Type\", \"RoomName\", \"ParticipantIdentity\", \"OccurredAt\" FROM \"LiveKitWebhookEvents\" ORDER BY \"CreatedAt\" DESC LIMIT 20;"
```

События пока только записываются (`ProcessedAt` пусто); сборка посещаемости и итог урока — этап 5.

**Ограничения локального стенда.**
- Звонок проверяется **только с этого компьютера**: в `livekit.yaml` задан `node_ip: 127.0.0.1`. Для телефона в той же сети замените его на LAN-адрес компьютера.
- Webhook идёт из контейнера на `host.docker.internal:5023`. На Windows и macOS этот адрес есть сам, на Linux он добавлен в `docker-compose.yml` через `extra_hosts`.
- В Development webhook по обычному `http` не перенаправляется на `https` (иначе самоподписанный сертификат ломал бы доставку); во всех остальных средах редирект работает как обычно.
- Проверка через мобильных операторов и TURN на 443/TLS на VPS отложена (`docs/DEFERRED.md`).

**Автотесты.** Приём webhook, выдача токена и страница покрыты тестами на настоящей БД; Cloudflare-подобной заглушки здесь нет: подпись формируется как у LiveKit (формат снят с настоящего сервера).
Сам звонок автотестами не проверяется (нужен браузер и контейнер LiveKit): его проверяют вручную по описанию выше.