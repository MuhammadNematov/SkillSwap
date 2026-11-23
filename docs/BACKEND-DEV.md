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
Без ключа JWT и без сертификата ключей шифрования (следующий раздел) приложение не запускается.

## 2.1. Ключи шифрования и сертификат

Переписка и токены писем шифруются ключами Data Protection (раздел 10.3 ТЗ). Сами ключи лежат в таблице `DataProtectionKeys`,
но **только в зашифрованном виде**: их защищает сертификат, который хранится вне БД. Украденная копия БД без сертификата бесполезна.
Приложение не запустится без исправного сертификата (нужен PFX с закрытым ключом, RSA не короче 2048 бит).

**Разработка: самоподписанный сертификат.** PowerShell (Windows), файл кладём вне репозитория:

```powershell
$dir = "$env:USERPROFILE\.skillswap"; New-Item -ItemType Directory -Force $dir | Out-Null
$bytes = New-Object byte[] 24; $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes)          # случайный пароль
$cert = New-SelfSignedCertificate -Subject "CN=SkillSwap Data Protection (dev)" -KeyAlgorithm RSA -KeyLength 3072 `
    -HashAlgorithm SHA256 -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
Export-PfxCertificate -Cert $cert -FilePath "$dir\dataprotection-dev.pfx" -Password (ConvertTo-SecureString $password -AsPlainText -Force)
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"   # в хранилище Windows копия не нужна
cd backend/src/SkillSwap.Api
dotnet user-secrets set "DataProtection:CertificatePath" "$dir\dataprotection-dev.pfx"
dotnet user-secrets set "DataProtection:CertificatePassword" $password
```

То же через OpenSSL (Linux/macOS/Git Bash):

```
openssl req -x509 -newkey rsa:3072 -sha256 -days 1825 -nodes -subj "/CN=SkillSwap Data Protection" -keyout dp.key -out dp.crt
openssl pkcs12 -export -inkey dp.key -in dp.crt -out dataprotection.pfx -passout pass:<пароль>
rm dp.key dp.crt
```

**macOS.** Системный `/usr/bin/openssl` — это LibreSSL, команды выше с ним работают как есть. Если стоит OpenSSL 3
(например, из Homebrew) и приложение при старте пишет «неверный пароль или это не файл PFX», пересоздай PFX
с флагом `-legacy` у `openssl pkcs12 -export` (OpenSSL 3 по умолчанию шифрует PFX по-новому, AES-256).
Каталог и user-secrets:

```
mkdir -p ~/.skillswap && cd ~/.skillswap   # здесь выполняем команды выше, файл назови dataprotection-dev.pfx
cd <репозиторий>/backend/src/SkillSwap.Api
dotnet user-secrets set "DataProtection:CertificatePath" "$HOME/.skillswap/dataprotection-dev.pfx"
dotnet user-secrets set "DataProtection:CertificatePassword" "<пароль>"
```

На macOS сертификат загружается с `DefaultKeySet`, а не `EphemeralKeySet` (macOS эфемерные ключи не поддерживает);
на Linux и Windows поведение прежнее.

**Настоящая почта (например, Gmail) с macOS.** Два препятствия, оба только локальные:

- Порт 587 бывает закрыт провайдером (таймаут подключения). Тогда `Smtp:Port=465` и `Smtp:Security=SslOnConnect`.
- macOS проверяет отзыв сертификата только по OCSP, а в сертификате `smtp.gmail.com` есть лишь адрес CRL. Проверка
  заканчивается «статус неизвестен», и MailKit рвёт соединение (`SslHandshakeException`, «remote certificate was rejected»).
  Тогда `dotnet user-secrets set "Smtp:CheckCertificateRevocation" "false"`: при старте в журнале будет предупреждение.
  Вне Development и Testing приложение с этой настройкой **не запустится** (`SmtpStartupCheck`). На Linux (боевой сервер)
  .NET проверяет CRL сам, настройка там не нужна.

Для Gmail нужен пароль приложения (аккаунт Google → безопасность → двухэтапная аутентификация → пароли приложений), а не
обычный пароль; `Smtp:FromAddress` должен совпадать с `Smtp:User`.

**Боевой сервер:** сертификат выпускается один раз и приходит в приложение из переменных окружения, файлов в репозитории нет.
Удобнее base64 вместо пути к файлу:

```
DataProtection__CertificateBase64=<содержимое PFX в base64>      # PowerShell: [Convert]::ToBase64String([IO.File]::ReadAllBytes("x.pfx"))
DataProtection__CertificatePassword=<пароль>
```

Задать нужно либо `CertificatePath`, либо `CertificateBase64` (оба сразу — ошибка при старте). Закрытый ключ загружается только в память
процесса и на диск сервера не пишется.

### Бэкап: обязательно

**Потеря сертификата = потеря всех зашифрованных данных (переписки, раздел 10.3 ТЗ).** Ключи в БД без него не расшифровать, а создать
такой же сертификат заново нельзя.

- Храните PFX **и** пароль в защищённом месте вне сервера (менеджер паролей/сейф), в двух независимых копиях. Пароль отдельно от файла.
- Резервная копия БД (ежедневная, раздел 11 ТЗ) и копия сертификата должны быть парой: восстановление БД без сертификата бесполезно.
- Старые ключи из БД не удаляются, поэтому **старые сертификаты тоже нужно хранить**, даже после замены на новый.
- Раз в месяц при тесте восстановления проверяйте, что ключи из копии БД расшифровываются сохранённым сертификатом.
- Не меняйте и не заменяйте сертификат «на лету»: приложение с другим сертификатом не прочитает ранее созданные ключи (данные станут нечитаемыми).
  Порядок замены сертификата ещё не сделан, он записан в `docs/DEFERRED.md`.
- При старте в журнал попадает предупреждение, если до конца срока сертификата осталось менее 30 дней.

Если в БД остались ключи, созданные до появления сертификата (открытые, без `EncryptedData`), они остаются читаемыми и не шифруются задним числом.
Локально их можно просто удалить (`delete from "DataProtectionKeys";`, пропадут только выданные ранее токены писем); на боевом сервере такое не должно случиться,
потому что приложение без сертификата не запускается.

## 3. Миграции

**Development: приложение применяет миграции само.** При запуске (`DevelopmentMigrationRunner`, регистрируется раньше сидеров и фоновых задач) оно смотрит, каких миграций не хватает в базе, и применяет их; в журнале видно, какие именно (`Development: применяются миграции (N): ...`) или что схема актуальна. После `git pull` достаточно перезапустить бэкенд, забытая миграция больше не даёт 500 на первом запросе к новой колонке. Если база недоступна, приложение не запускается (так же, как при пустой строке подключения).

**Production и любая другая среда (в том числе Testing): приложение схему не меняет.** Миграции применяются отдельной командой **при деплое, до запуска новой версии**; у пользователя БД, под которым работает приложение, не должно быть прав на изменение схемы. Нужен либо `dotnet ef`, либо SQL-скрипт:

```
cd backend
# применить к базе напрямую (нужна строка подключения в ConnectionStrings__Default и SDK на сервере)
dotnet ef database update --project src/SkillSwap.Infrastructure --startup-project src/SkillSwap.Api

# или собрать идемпотентный скрипт (можно запускать повторно) и выполнить его psql от имени владельца схемы
dotnet ef migrations script --idempotent --project src/SkillSwap.Infrastructure --startup-project src/SkillSwap.Api -o migrate.sql
```

Правила для миграций: каждая меняет схему обратимо (метод `Down`), новая колонка либо допускает null, либо имеет значение по умолчанию (во время деплоя старая версия приложения ещё работает на уже изменённой схеме); тест `DevelopmentMigrationTests` проверяет оба поведения на пустых базах (Development догоняет схему, другие среды её не трогают). Тесты и CI применяют миграции сами до старта приложения (`ApiFactory`).

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

## Фоновые задачи (Hangfire), почта и Mailpit (этап 4)

### Что есть
- **Hangfire** выполняет фоновые задачи: письма, а в следующих шагах напоминания, истечение заявок и слотов, завершение обменов по сроку. Очередь лежит в PostgreSQL, в схеме `hangfire` той же БД (создаётся сама при первом запуске). Сбойные задачи повторяются.
- **Письма** уходят в фоне: запрос только ставит письмо в очередь (`IMailSender`), а задача отправляет его по SMTP. Тело письма (в нём ссылки с токенами) лежит в очереди зашифрованным (Data Protection), в панели Hangfire оно не читается. Сбой SMTP повторяется до 5 раз (через 10 с, 30 с, 2 мин, 10 мин, 1 ч), потом задача остаётся в списке неудачных.
- **Утечка по времени в `forgot-password` и `resend-confirmation` закрыта.** Письмо больше не отправляется в запросе, а для неизвестного адреса в очередь попадает «пустая» задача: в обоих случаях ровно одна запись в очередь, поэтому по времени ответа нельзя узнать, зарегистрирован ли адрес.
- **`IUnitOfWork`** (Application): `ExecuteAsync` открывает транзакцию, `LockUsersAsync` блокирует строки пользователей по возрастанию Id (раздел 10.2 ТЗ: лимиты и пересечения уроков). Пара в любом порядке не даёт взаимной блокировки; это проверяет тест на 40 параллельных транзакциях.

### Локальный запуск
1. `docker compose up -d mailpit` (SMTP на порту 1025, письма в браузере: http://localhost:8025).
2. Один раз в user-secrets:

   ```
   cd backend/src/SkillSwap.Api
   dotnet user-secrets set "Smtp:Host" "localhost"
   dotnet user-secrets set "Smtp:Port" "1025"
   dotnet user-secrets set "Smtp:Security" "None"
   ```

3. Запустите API: письма появляются в Mailpit примерно через секунду. Панель фоновых задач открывается только суперадмину по одноразовому билету (`POST /superadmin/hangfire/session`, затем ссылка из ответа, раздел «Журнал аудита и панель Hangfire»); сидера суперадмина в Development пока нет, он появится в шаге 10.

Без `Smtp:Host` в Development письма пишутся в консоль (как раньше).

### Настройки
| Ключ (переменная окружения) | Значение |
| --- | --- |
| `Smtp:Host` (`Smtp__Host`) | Адрес SMTP-сервера. Пусто — письма в консоль (только Development и Testing) |
| `Smtp:Port`, `Smtp:Security` | Порт и защита: `None` (только локально), `StartTls` (по умолчанию, порт 587), `SslOnConnect` (порт 465) |
| `Smtp:User`, `Smtp:Password` | Логин и пароль, **только в переменных окружения или user-secrets** |
| `Smtp:FromAddress`, `Smtp:FromName` | Отправитель |
| `Hangfire:Enabled` | `true` по умолчанию; `false` — задачи выполняются сразу в процессе (так работают тесты). На боевом сервере всегда `true` |
| `Hangfire:WorkerCount`, `Hangfire:QueuePollIntervalSeconds` | Число одновременных задач (4) и частота опроса очереди (2 с) |

**Вне Development и Testing приложение не запустится без настоящего SMTP**: `Smtp:Host` обязателен, `Security: None` для удалённого сервера запрещён (пароль и токены ушли бы открытым текстом), адрес отправителя должен быть настоящим адресом. Панели Hangfire на боевом сервере нет, она появится для суперадмина на этапе 7.

**Настоящая почта локально (Gmail).** Нужна двухэтапная проверка в аккаунте Google и «пароль приложения» (https://myaccount.google.com/apppasswords): обычный пароль Gmail не подходит. Всё задаётся в user-secrets, в репозиторий ничего не попадает:

```
cd backend
dotnet user-secrets set "Smtp:Host" "smtp.gmail.com" --project src/SkillSwap.Api
dotnet user-secrets set "Smtp:Port" "587" --project src/SkillSwap.Api
dotnet user-secrets set "Smtp:Security" "StartTls" --project src/SkillSwap.Api
dotnet user-secrets set "Smtp:User" "<ваш адрес>@gmail.com" --project src/SkillSwap.Api
dotnet user-secrets set "Smtp:Password" "<пароль приложения, 16 символов без пробелов>" --project src/SkillSwap.Api
dotnet user-secrets set "Smtp:FromAddress" "<ваш адрес>@gmail.com" --project src/SkillSwap.Api
```

`FromAddress` должен совпадать с адресом Gmail (иначе Gmail подменит отправителя). Если письма всё равно идут в Mailpit, проверьте переменные окружения `Smtp__Host` и т. п.: они сильнее user-secrets. После смены настроек перезапустите бэкенд. Ссылка в письме ведёт на `Frontend:BaseUrl` (локально `http://localhost:5173`), то есть открывается только на вашем компьютере.

### Как написать фоновую задачу
```csharp
// Application: поставить в очередь (аргументы простые: id, строки; секреты открытым текстом нельзя, они лежат в БД)
_jobs.Enqueue<ReminderJobs>(j => j.SendAsync(sessionId));
_jobs.Schedule<ReminderJobs>(j => j.SendAsync(sessionId), startAt.AddHours(-24));
```
Класс задачи регистрируется в DI (`AddScoped`), метод публичный и **идемпотентный**: перед действием проверяет текущее состояние, повторный запуск ничего не портит. В тестах (`Hangfire:Enabled=false`) `Enqueue` выполняется сразу, а отложенные задачи запускает тест: `InlineBackgroundJobs.RunDueAsync(now)`.

## Контракт API: `docs/openapi.json`

Схема OpenAPI всего API лежит в репозитории: **`docs/openapi.json`**. Её порождает само приложение, руками файл не правится. Фронтенд берёт схему оттуда (например, для генерации типов), не запуская бэкенд.

- **Проверка актуальности.** Тест `OpenApiSnapshotTests` на каждом push в CI сравнивает файл с тем, что приложение отдаёт в Development (`/openapi/v1.json`). Изменился API, а файл нет: CI красный, а в сообщении указана первая отличающаяся строка.
- **Как обновить** (после любого изменения эндпоинтов, DTO, кодов ответов или комментариев `<summary>` у методов):

  ```
  cd backend
  UPDATE_OPENAPI=1 dotnet test tests/SkillSwap.Api.IntegrationTests --filter OpenApiSnapshotTests
  ```

  В PowerShell: `$env:UPDATE_OPENAPI='1'`, затем команда `dotnet test` с тем же фильтром, затем `Remove-Item Env:UPDATE_OPENAPI`. Результат коммитится вместе с изменением API.
- В схеме только то, что видно в Scalar: инструменты разработчика LiveKit (`/dev/livekit`) и секреты в неё не попадают (это проверяет тот же тест).
- Схема описывает формы запросов и ответов и коды HTTP. Правила, которых из схемы не видно (порядок онбординга, коды `code` в ошибках, смысл полей), описаны в разделах этого файла и в `docs/API-ANSWERS.md`.

## Ссылки из писем (подтверждение email и сброс пароля)

Фронтенду нужны две страницы: `/confirm-email` и `/reset-password`. Письма ведут на них такими ссылками:

```
{Frontend:BaseUrl}/confirm-email?userId=<guid>&token=<token>
{Frontend:BaseUrl}/reset-password?userId=<guid>&token=<token>
```

- **Параметры:** ровно два, `userId` (GUID пользователя) и `token`.
- **Адрес `Frontend:BaseUrl`** берётся из настроек (на сервере переменная `Frontend__BaseUrl`), косая черта в конце не мешает. В `appsettings.json` стоит `http://localhost:5173` (порт Vite в разработке). Вне Development и Testing приложение не запустится, если адрес не https или указывает на localhost: иначе пользователям ушли бы письма со ссылкой на «localhost».
- **Токен URL-безопасный (Base64Url):** только символы `A–Z a–z 0–9 - _`, без `+ / =` и без процентного кодирования. Фронтенд берёт `token` из адреса как есть и отправляет на сервер без перекодирования.
- **Куда отправлять** (оба запроса публичные, ответы и коды ошибок смотрите в Swagger/Scalar):
  - `POST /api/v1/auth/confirm-email` с телом `{ "userId": "...", "token": "..." }`;
  - `POST /api/v1/auth/reset-password` с телом `{ "userId": "...", "token": "...", "newPassword": "..." }`.
- Токен одноразовый. Подтверждение email живёт 1 сутки (срок Identity по умолчанию), сброс пароля — 1 час. Неверный, просроченный или уже использованный токен даёт 400 с кодом ошибки; страница должна предложить запросить письмо заново (`/auth/resend-confirmation`, `/auth/forgot-password`).
- Старый вид токена (обычный Base64 с `+ / =`) сервер тоже принимает: письма, отправленные раньше, продолжают работать.

## Подтверждение email кодом (вместе со ссылкой)

Письмо подтверждения (регистрация и «отправить ещё раз») содержит **и ссылку, и шестизначный код**: подойдёт любой способ. Код нужен, когда письмо открыто на другом устройстве (ссылка ведёт на адрес фронтенда, и копировать её неудобно).

`POST /auth/confirm-email/code`, тело `{ "email": "…", "code": "123456" }`, ответ 204 без тела. Нужен только email, который человек ввёл при регистрации (на странице «письмо отправлено» он уже известен).

- Код: шесть цифр, **15 минут**, **5 попыток**. Пробелы и дефисы в коде допустимы («123 456», «123-456»).
- **Новое письмо отменяет прежний код**, счётчик попыток обнуляется. После пяти неверных попыток код закрыт, нужно нажать «отправить ещё раз» (лимит писем: 3 в час на адрес).
- Любая причина отказа (адреса нет, код неверный, истёк, попытки кончились, почта уже подтверждена) — одна и та же 400 `invalid_confirmation_code`: по ответу нельзя узнать, зарегистрирован ли адрес. Код не из шести цифр — 400 `validation_failed`, попытка при этом не списывается.
- Лимит частоты: 429 с `Retry-After`, **20 запросов в час с одного IP**, счётчик общий со ссылкой (`confirm-email`).
- Подтверждение ссылкой, как и раньше, `POST /auth/confirm-email` с `userId` и `token`. Если почта подтверждена ссылкой, код перестаёт действовать, и наоборот.
- В базе хранится только хеш кода (SHA-256 с привязкой к пользователю), а не сам код.
- Изменение ТЗ: FR-1 говорит «одноразовой ссылкой», теперь ссылка и код. Внести в ТЗ при следующей правке.

**Что нужно фронтенду:** на странице «письмо отправлено» (после регистрации) рядом с полем для ссылки добавить поле «Код из письма» (6 цифр) и вызывать этот эндпоинт; после 204 действовать как после подтверждения по ссылке (вход, дальше онбординг). Ошибка `invalid_confirmation_code` — показать «Код неверный или устарел» и предложить отправить письмо ещё раз.

## Профиль, анкета, каталог, подбор, блокировки (этап 3): контракт для фронтенда

Все пути с префиксом `/api/v1`, все даты в UTC. Подробные схемы запросов и ответов смотрите в Scalar (`/scalar`, только Development): здесь то, чего из схем не видно, то есть правила и коды ошибок. Ошибки приходят в формате ProblemDetails с полем `code` (по нему подбирается перевод); ошибки проверки полей (`validation_failed`) содержат ещё `errors`: поле → список кодов.

### Эндпоинты

| Метод и путь | Назначение |
| --- | --- |
| `GET /me`, `PUT /me` | Мой профиль и его правка |
| `GET /users/{id}` | Профиль другого пользователя |
| `POST /users/{id}/block`, `DELETE /users/{id}/block`, `GET /me/blocks` | Блокировки |
| `GET /languages` | Справочник языков занятий (без входа, кэшируется на сутки) |
| `GET /skill-categories`, `GET /skills` | Каталог навыков |
| `GET /me/skills`, `PUT /me/skills` | Анкета: «Умею» (`teach`) и «Хочу изучать» (`learn`) |
| `POST /skills/suggestions` | Предложить новый навык |
| `GET /matches` | Подбор |
| `/admin/skills`, `/admin/skill-categories` | Правка каталога (админ и суперадмин) |

`DELETE /me` и `POST /me/export` появятся на этапе 4.

### Порядок онбординга

1. После входа `GET /me`: поле `skillsComplete` (`false` — отправить на анкету). **Анкета полная (`skillsComplete: true`), только когда есть все три части:** хотя бы один навык в «Умею», хотя бы один в «Хочу изучать» и **хотя бы один язык занятий** в профиле. Без языка подбор был бы пуст из-за обязательного общего языка, поэтому такая анкета полной не считается.
2. Профиль: `PUT /me` (имя, часовой пояс IANA, **хотя бы один язык занятий** (без него анкета неполная), язык интерфейса). Языки берутся из `GET /languages`.
3. Анкета: `GET /skill-categories` → `GET /skills?category=&q=` → `PUT /me/skills`. Если нужного навыка нет, `POST /skills/suggestions`.
4. Когда анкета становится полной (последняя недостающая часть: навык или язык, в любом порядке), сервер сам включает показ в подборе (`isSearchable: true`). Если пользователь потом сам выключил показ через `PUT /me`, сервер больше не включает его автоматически. Если анкета перестала быть полной (например, убрали все языки или все навыки одного направления), сервер сам скрывает пользователя (`isSearchable: false`) и отвечает 200: это не ошибка; когда анкета снова станет полной, показ вернётся (если человек не скрывался сам).
5. `GET /matches` доступен без показа в подборе: нужна лишь полная анкета.

### Профиль

- `PUT /me` заменяет поля целиком: `name` (1–100), `bio` (до 1000, пусто — очистить), `uiLanguage` (`tg`, `ru`, `en`), `timeZone` (IANA, ровно как в списке систем, например `Asia/Dushanbe`; `asia/dushanbe` и `Tajikistan Standard Time` не принимаются), `languages` (коды из `/languages`, до 10, обязательно, может быть пустым), `isSearchable` (обязательно).
- Не меняются через `PUT /me`: email, дата рождения, роль, рейтинг, аватар (загрузка аватара — этап 4; `avatarUrl` пока всегда `null`).
- **Язык занятий и язык интерфейса — разные поля.** Языки занятий (`languages`) — любые ISO 639-1; язык интерфейса — только `tg`, `ru`, `en`.
- `GET /me` содержит `isSearchable`, `languages`, `skillsComplete` (см. выше: навыки в обоих направлениях и язык), рейтинг и `isNewcomer` (меньше 3 отзывов: `ratingAvg` равен `null`, показывать «Новичок»).
- Если `PUT /me` просит `isSearchable: true`, а анкета неполная (в том числе нет языка): 403 `skills_incomplete`, ничего не сохраняется. Если пользователь уже виден, а этим сохранением анкета стала неполной (убрали все языки), показ тихо выключается, ответ 200.
- `GET /users/{id}`: имя, о себе, аватар, часовой пояс, языки, навыки (`teach` и `learn`, только включённые), `isMinor` (16–17 лет), рейтинг. Без email и даты рождения. **404 `user_not_found`** приходит и когда пользователя нет, и когда он скрыт, заблокирован, удалён, служебный, связан с вами блокировкой или из другой возрастной группы: различить причины нельзя намеренно.

### Каталог и анкета

- `GET /skills?category=&q=&lang=&page=&pageSize=`: поиск по подстроке в названии на tg, ru, en и в синонимах, без учёта регистра. `lang` — язык сортировки (`tg`, `ru`, `en`, по умолчанию `en`).
- Названия приходят на всех трёх языках (`nameTg`, `nameRu`, `nameEn`), язык выбирает фронтенд.
- `PUT /me/skills`: оба списка `teach` и `learn` обязательны (пустой очищает направление). У записи: `skillId`, `level` (`Beginner`, `Intermediate`, `Advanced`), `description` (до 500). Навык один раз в направлении (но может быть и в «Умею», и в «Хочу изучать»), не больше 30 в направлении. Список заменяется целиком.
- В `GET /me/skills` у навыка есть `isActive`: `false` — админ выключил навык после выбора; предложите убрать его при следующем сохранении.
- Ответы `GET` и `PUT /me/skills` содержат `isComplete` (то же, что `skillsComplete`: нужны и навыки, и язык) и `isSearchable`.
- `POST /skills/suggestions` (`categoryId`, `name` 2–100 символов с буквой): 201 со статусом `Pending`. Если такой навык уже есть в категории (в том числе как синоним), 409 `skill_already_exists` и в ответе **`existingSkillId`**: предложите добавить существующий навык. Лимиты: 5 предложений в час (429) и не больше 10 неразобранных (409 `too_many_pending_suggestions`).

### Подбор `GET /matches`

Параметры: `skill` (id навыка из моего «Хочу изучать»), `lang` (ISO 639-1), `level` (`Beginner`, `Intermediate`, `Advanced` — уровень, на котором партнёр преподаёт нужный мне навык), `minRating` (0–5; «Новички» при значении больше 0 не показываются), `page`, `pageSize` (до 50).

- В выдаче те, кто **преподаёт то, что я хочу изучать**, на уровне не ниже моего. **Общий язык занятий со мной обязателен**: без него человека в выдаче нет. Поэтому пользователю без своих языков подбор закрыт: 403 `skills_incomplete` (а не пустая выдача без объяснения).
- `matchType`: `Full` (ему нужно то, что умею я, на уровне не выше моего) или `Partial` (встречного интереса нет: свой навык предлагаю в тексте заявки). Полные идут выше.
- Порядок: полный матч; меньшая разница часовых поясов (`timeZoneDifferenceHours`); выше уровень партнёра; выше рейтинг («Новичок» ниже оценённых); активность за 7 дней; больше совпавших навыков.
- В карточке: `theyTeach` (что он преподаёт из моего «Хочу изучать», с `theirLevel` и `myLevel`), `theyLearn` (что ему нужно из моего «Умею»; пусто у `Partial`), `commonLanguages`, `languages`, `isMinor`, рейтинг. Человек в выдаче один раз, сколько бы навыков ни совпало.
- Не показываются: вы сами, скрытые, заблокированные и удалённые, служебные аккаунты, связанные с вами блокировкой в любую сторону, из другой возрастной группы (пока включена настройка `Matching.MinorsMatchOnlyMinors`: 16–17 лет только друг с другом, остальные только друг с другом).
- Пока анкета неполная (нет навыков в обоих направлениях или не указан язык занятий): 403 `skills_incomplete`, отправляйте на онбординг.

### Блокировки

- `POST /users/{id}/block` и `DELETE /users/{id}/block` отвечают 204 и безопасны при повторе. Блокировка действует **в обе стороны**: заблокировавший и заблокированный не видят друг друга в профилях и в подборе. Заблокированный об этом не узнаёт.
- `GET /me/blocks`: кого я заблокировал (чтобы можно было разблокировать: профиль заблокированного через `GET /users/{id}` недоступен). Не больше 200 блокировок (409 `too_many_blocks`).

### Коды ошибок этапа

| HTTP | `code` | Когда |
| --- | --- | --- |
| 400 | `validation_failed` | Неверные поля тела запроса (см. `errors`) |
| 400 | `invalid_paging` | `page` меньше 1 или `pageSize` вне 1–50 |
| 400 | `unsupported_language` | `lang` в `/skills` не `tg`, `ru`, `en` |
| 400 | `query_too_long` | `q` длиннее 100 символов |
| 400 | `invalid_language`, `invalid_level`, `invalid_min_rating` | Плохие фильтры `/matches` |
| 400 | `unknown_skill` | В анкете навык, которого нет или который выключен |
| 400 | `unknown_category` | Категории нет или она выключена (предложение навыка) |
| 400 | `cannot_block_self` | Блокировка самого себя |
| 403 | `skills_incomplete` | Анкета неполная (нет навыка в «Умею», в «Хочу изучать» или языка занятий): нет подбора и показа в подборе |
| 403 | `regular_account_required` | Служебный аккаунт пользуется функцией обычного |
| 403 | `staff_has_no_profile` | Служебный аккаунт правит профиль или анкету |
| 403 | `account_not_active` | Аккаунт заблокирован администратором |
| 404 | `user_not_found` | Пользователя нет или он вам недоступен |
| 409 | `skill_already_exists` | Предложение дублирует навык каталога (`existingSkillId`) |
| 409 | `suggestion_already_pending` | Вы уже предложили этот навык |
| 409 | `too_many_pending_suggestions` | Больше 10 неразобранных предложений |
| 409 | `too_many_blocks` | Больше 200 блокировок |
| 409 | `concurrency_conflict` | Параллельное изменение профиля или анкеты: повторить |
| 429 | `rate_limited` | Слишком часто; заголовок `Retry-After` в секундах |

Для админов (`/admin/skills`, `/admin/skill-categories`) дополнительно: 409 `skill_name_taken`, `category_name_taken`, `category_not_empty`, `skill_in_use`; 404 `skill_not_found`, `category_not_found`.

## Заявки на обмен (этап 4, шаг 1): контракт для фронтенда

Все методы требуют входа и обычного аккаунта (служебному — 403 `regular_account_required`). Полная схема — `docs/openapi.json`, тег **Exchange requests**.

| Метод | Что делает |
| --- | --- |
| `POST /exchange-requests` | Отправить заявку: `toUserId`, `toTeachesSkillId` (обязателен, навык из «Умею» получателя), `fromTeachesSkillId` (необязателен: свой навык из «Умею»), `durationDays` (30 или 60), `message` (до 1000 символов, необязательно). 201 + заявка |
| `GET /exchange-requests?box=in\|out` | Входящие или исходящие, новые сверху. `box` обязателен. Фильтр `status`, страницы `page`/`pageSize` (до 50): `{ items, page, pageSize, totalCount }` |
| `GET /exchange-requests/{id}` | Одна заявка (участнику) |
| `POST /exchange-requests/{id}/accept` | Принять (получатель). Тело `{ "fromTeachesSkillId": … }` обязательно, если отправитель не указал свой навык: выбрать из его «Умею» (список — в публичном профиле отправителя) |
| `POST /exchange-requests/{id}/decline` | Отклонить (получатель) |
| `POST /exchange-requests/{id}/withdraw` | Отозвать (отправитель) |

Заявка: `id`, `direction` (`in` — мне, `out` — от меня), `status` (`Pending`, `Accepted`, `Declined`, `Withdrawn`, `Expired`), `fromUser`/`toUser` (`id`, `name`, `avatarUrl`), `fromTeachesSkill` (**null**, пока получатель не выбрал при принятии) и `toTeachesSkill` (`id`, `nameTg`, `nameRu`, `nameEn`), `durationDays`, `message`, `expiresAt` (создание + 7 дней), `respondedAt`, `createdAt`. После принятия `fromTeachesSkill` всегда заполнен.

Ошибки (поле `code`):
- 404 `user_not_found` при отправке: получатель скрыт, удалён, заблокирован в любую сторону или другой возрастной группы. Заблокированный не узнаёт о блокировке.
- 403 `skills_incomplete`: моя анкета неполная, фронтенд ведёт на онбординг.
- 400 `skill_not_taught_by_recipient`, `skill_not_taught_by_sender`, `cannot_request_self`, `validation_failed` (срок не 30/60, длинное сообщение), при принятии `from_skill_required` и `from_skill_fixed` (отправитель уже указал навык, заменить нельзя), `invalid_box`.
- 409 при отправке: `request_already_pending` и `reverse_request_pending` (в ответе `requestId`: открыть существующую заявку), `request_cooldown` (в ответе `retryAfter`: после отказа нельзя писать тому же человеку 30 дней), `too_many_pending_requests` (не больше 5 ожидающих исходящих).
- 429 `daily_request_limit` (не больше 10 новых заявок за сутки).
- 409 при ответе: `invalid_state_transition` (заявка уже не ждёт ответа), `request_expired`, `skill_no_longer_taught` (получатель убрал навык: можно только отклонить), `concurrency_conflict` (одновременное изменение: повторить).
- 403 `not_request_recipient` / `not_request_sender` (не та сторона), 404 `request_not_found` (чужая или несуществующая).

Лимиты (3 активных обмена, 5 ожидающих, 10 в сутки, 30 дней кулдауна) лежат в `SystemSettings` (`Requests.*`), правка суперадмином появится на этапе 7. Через 7 дней без ответа фоновая задача переводит заявку в `Expired`; просроченная, но ещё не закрытая задачей заявка в лимитах уже не считается, а принять её нельзя. Принятие создаёт обмен в той же транзакции (поле `exchangeId` заявки), см. раздел «Обмены» ниже.

## Обмены (этап 4, шаг 2): контракт для фронтенда

Обмен появляется при принятии заявки (`POST /exchange-requests/{id}/accept`, в ответе `exchangeId`): принятие и создание обмена идут в одной транзакции. Тег в `docs/openapi.json` — **Exchanges**. Доступ только у участников, чужой обмен — 404 `exchange_not_found`.

| Метод | Что делает |
| --- | --- |
| `GET /exchanges` | Мои обмены, новые сверху; фильтр `status` (`Active`, `Completed`, `Aborted`, `Frozen`), `page`/`pageSize` |
| `GET /exchanges/{id}` | Карточка обмена |
| `POST /exchanges/{id}/complete` | «Завершить». Когда нажали оба, обмен `Completed`; повторное нажатие не ошибка |
| `POST /exchanges/{id}/abort` | Выйти досрочно: тело `{ "reason": "…" }` обязательно (до 500 символов). `Aborted`, `abortedBy: "me"`. Причина ровно `partner_no_show` — выход из-за неявок партнёра, см. «Выход после неявок партнёра» ниже |
| `POST /exchanges/{id}/extend` | Согласие на продление на 30 дней; продление применяется, когда согласны оба (одно за обмен) |

Карточка показывает всё глазами вызывающего: `partner` (`id`, `name`, `avatarUrl`), `iSkillTeach` (чему учу я), `iSkillLearn` (чему учит партнёр), `startDate`, `endDate`, `isExtended`, `completedByMe`/`completedByPartner`, `myExtensionAccepted`/`partnerExtensionAccepted`, `frozenAt`, `abortedBy` (`me`, `partner` или `null`: прервала система при блокировке либо модератор), `abortReason`, `closedAt`, `balance` (`myLessons`, `partnerLessons`, `showReminder`: разница больше 3), `requestId`. Баланс считается по засчитанным урокам (раздел «Уроки»).

Ошибки (`code`): 409 `invalid_state_transition` (обмен не активен: завершён, прерван, заморожен), 400 `validation_failed` (нет причины или она длиннее 500 символов) и `already_extended`, 404 `exchange_not_found`.

**Лимит активных обменов (3, активные и замороженные).** Проверяется при отправке заявки (у отправителя) и при принятии (у обоих, под блокировкой): 409 `exchange_limit_reached` с полем `limitReachedFor`: `you` — лимит у вас, `partner` — у собеседника. При отказе при принятии заявка остаётся `Pending`, обмен не создаётся. Между двумя людьми одновременно один обмен: повторная заявка — 409 `exchange_already_active`. Если отправитель к моменту принятия убрал навык из «Умею», 409 `sender_skill_no_longer_taught` (заявку можно отклонить).

**Блокировка и подбор (шаг 3).** `POST /users/{id}/block` в одной транзакции закрывает всё между двумя людьми: ожидающие заявки в любую сторону становятся `Expired`, активный обмен становится `Aborted` с `abortedBy: null` (виновного нет). `abortReason` у того, кто заблокировал, — `blocked`, у заблокированного — нейтральная `partner_unavailable` (факт блокировки ему не раскрывается; в БД хранится настоящая причина для модератора). Тексты для обоих значений нужны во фронтенде. Замороженный обмен блокировка не прерывает (им занимается модератор). Снятие блокировки ничего не восстанавливает. В `GET /matches` не попадают люди, с которыми у вас ожидающая заявка (в любую сторону, срок не вышел) или активный/замороженный обмен; после отказа, отзыва, истечения или завершения человек снова виден.

Срок: `endDate` = принятие + 30 или 60 дней. По его истечении обмен сам становится `Completed` (фоновая задача, замороженный по сроку не завершается; после продления задача ставится на новый срок).

## Уведомления (этап 4, шаг 4): контракт для фронтенда

**REST** (тег **Notifications**, нужен вход): `GET /notifications?unreadOnly=&page=&pageSize=` (новые сверху; `{ items, page, pageSize, totalCount, unreadCount }`), `GET /notifications/unread-count` (`{ unreadCount }`, для значка), `POST /notifications/{id}/read` (повтор не ошибка; чужое и несуществующее — 404 `notification_not_found`), `POST /notifications/read-all` (`{ marked }`).

Уведомление: `id`, `type`, `payload` (объект, состав зависит от типа), `isRead`, `createdAt`, `readAt`. Текст строит фронтенд по `type` и `payload` на языке интерфейса.

**Хаб реального времени**: `/hubs/notifications` (SignalR, `@microsoft/signalr`). Подключение с токеном: в браузере через `accessTokenFactory` (токен уходит параметром `access_token`, браузерный WebSocket не умеет заголовок); без токена или с плохим токеном подключение отклоняется (401). Сервер шлёт событие **`notification`** с объектом уведомления (такой же, как элемент списка; перечисления строками). Клиент ничего не вызывает. Каждый получает только свои уведомления, на все открытые вкладки. Уведомление создаётся сразу при действии, доставка по хабу идёт фоновой задачей (доли секунды). При переподключении клиент запрашивает `GET /notifications?unreadOnly=true`, чтобы не пропустить пришедшее за время обрыва; дубли по `id` игнорировать.

| `type` | Кому | Когда | `payload` |
| --- | --- | --- | --- |
| `RequestReceived` | получателю | отправлена заявка | `requestId`, `userId`, `userName` (отправитель), `durationDays` |
| `RequestAccepted` | отправителю | заявку приняли | `requestId`, `exchangeId`, `userId`, `userName` (кто принял) |
| `RequestDeclined` | отправителю | заявку отклонили | `requestId`, `userId`, `userName` |
| `RequestExpired` | отправителю | 7 дней без ответа | `requestId`, `userId`, `userName` (получатель) |
| `ExchangeFinishRequested` | партнёру | один нажал «Завершить» | `exchangeId`, `userId`, `userName` |
| `ExchangeCompleted` | обоим | оба нажали «Завершить» или срок вышел | `exchangeId` |
| `ExchangeAborted` | партнёру | вышел досрочно: `reason` — его текст | `exchangeId`, `userId`, `userName`, `reason` |
| `ExchangeAborted` | заблокированному | партнёр заблокировал | `exchangeId`, `reason: "partner_unavailable"` (без имени) |
| `ExtensionProposed` | партнёру | один согласился продлить | `exchangeId`, `userId`, `userName` |
| `ExchangeExtended` | обоим | продление применено | `exchangeId`, `endDate` |
| `SessionProposed`, `SessionConfirmed`, `SessionDeclined`, `SessionCancelled`, `SessionExpired`, `SessionReminder` (письмо), `SessionNeedsConfirmation`, `SessionCompleted`, `SessionNotCounted` | участникам урока | см. раздел «Уроки» | `sessionId`, `exchangeId`, `startAt`, `userId`, `userName` |
| `NewMessages` | получателю сообщений | есть непрочитанные в чате обмена; одно уведомление на обмен, обновляется | `exchangeId`, `userId`, `userName`, `count` |

Блокировка не создаёт уведомлений блокировавшему и не уведомляет об истёкших из-за блокировки заявках: факт блокировки не раскрывается. Новые типы (уроки, напоминания, обращения) добавятся в следующих шагах.

**Письма-дубли** получают только важное: новая заявка (`RequestReceived`) и принятая заявка (`RequestAccepted`); позже напоминания об уроках и решения по обращениям. Всё остальное только в интерфейсе. Письмо уходит на языке интерфейса пользователя (`tg`, `ru`, `en`; любой другой — английский; **это касается и писем подтверждения email, сброса пароля и «пароль изменён»**) и только на подтверждённый адрес, один раз на уведомление. Таджикские тексты ждут вычитки носителем (`docs/DEFERRED.md`). Ссылки ведут на страницы фронтенда; маршруты настраиваются рядом с `Frontend:BaseUrl`, без правки кода: `Frontend:RequestsPath` (по умолчанию `/requests`, письмо «новая заявка») и `Frontend:ExchangePath` (по умолчанию `/exchanges/{id}`, письмо «заявка принята»; `{id}` заменяется на id обмена). Переменные окружения: `Frontend__RequestsPath`, `Frontend__ExchangePath`. Пути должны начинаться с «/» (для обмена с `{id}`), иначе приложение не запустится.

**Как создавать уведомления в коде:** `INotificationPublisher.Add(userId, type, payload)` внутри транзакции (сохраняется вместе с изменением) и `Dispatch()` после её фиксации (ставит фоновые задачи доставки и письма). Класс `NotificationPolicy` решает, какие типы дублируются письмом.

## Чат обмена (этап 4, шаг 5): контракт для фронтенда

Чат работает через хаб **`/hubs/chat`** (SignalR, `@microsoft/signalr`) и два REST-метода (тег **Chat**). Токен: `accessTokenFactory` (параметр `access_token`) или заголовок `Authorization`; без токена соединение отклоняется (401). Одна группа на обмен; при подключении (и при каждом переподключении) сервер **сам** добавляет вас в группы всех ваших обменов, чей чат виден. Для обмена, созданного уже после подключения (вы только что приняли заявку), вызовите `Join(exchangeId)`. После переподключения за пропущенным сходите в историю (`GET …/messages`), события за время обрыва не доставляются повторно.

**Методы хаба** (клиент вызывает `connection.invoke`): `Join(exchangeId)`, `Leave(exchangeId)`, `Send(exchangeId, text)` → сообщение, `Edit(messageId, text)` → сообщение, `Delete(messageId)` → `{ exchangeId, messageId, deletedAt }`, `Typing(exchangeId)`, `Read(exchangeId, upToMessageId)` (`null` — все) → `{ exchangeId, readerId, upToMessageId, marked, readAt }`. Ошибка приходит исключением, в тексте которого машинный код (`error.message.includes("chat_read_only")`).

**События от сервера:** `message` и `messageEdited` (сообщение целиком), `messageDeleted` (`{ exchangeId, messageId, deletedAt }`), `messagesRead` (`{ exchangeId, readerId, upToMessageId, marked, readAt }`: собеседник прочитал, подсветить «прочитано»), `typing` (`{ exchangeId, userId }`, только собеседнику; сервер пропускает чаще раза в 2 секунды с одного соединения). Все события, кроме `typing`, получает и сам автор на других вкладках.

**Сообщение:** `id`, `exchangeId`, `senderId` (null у системных), `type` (`Text`; `Voice`, `File`, `SessionCard`, `System` придут в шагах 6 и 7), `text` (null у удалённого), `paymentWarning`, `attachmentId`, `sessionId`, `createdAt`, `editedAt`, `deletedAt`, `readAt`. Статусы: сохранено на сервере = «отправлено» (приходит в ответе `Send`), `readAt` ≠ null = «прочитано». Отдельного «доставлено» нет.

**История:** `GET /exchanges/{id}/messages?before=&limit=` (`limit` 1–100, по умолчанию 30). Без `before` — самые новые; ответ `{ items, nextBefore }`: `items` по возрастанию времени, `nextBefore` — курсор следующей, более старой страницы (его и передаём в `before`), `null`, когда истории больше нет. Курсор непрозрачный. `POST /exchanges/{id}/messages/read` (тело `{ "upToMessageId": … }` или без тела) — то же, что `Read` в хабе, работает и когда чат только для чтения.

**Правила:**
- Текст до 4000 символов, пробелы по краям убираются (`message_empty`, `message_too_long`). Не больше 20 сообщений за 10 секунд (`chat_rate_limited`, 429).
- Править и удалять можно только своё сообщение в течение 15 минут (`not_message_author`, `edit_window_expired`, `message_deleted`). Прежний текст сохраняется в `MessageRevisions` для модератора. Правка с тем же текстом правкой не считается.
- **Антифрод:** если в тексте номер банковской карты (проверка контрольной суммы) или призыв к оплате (ru, tg, en), у сообщения `paymentWarning: true`: фронтенд показывает обоим предупреждение о запрете денежных расчётов. Сообщение не блокируется. Проверка идёт до шифрования, при правке пересчитывается.
- **Шифрование:** текст в БД лежит зашифрованным (Data Protection), в дампе его не прочитать.
- **Режимы** (поле `chat` в карточке обмена): `ReadWrite` — обмен активен; `ReadOnly` — обмен заморожен (`readOnlyReason: "frozen"`) либо завершён или прерван не более 30 дней назад (`"completed"` / `"aborted"`, `readableUntil`); `Archived` — прошло больше 30 дней, участникам чат недоступен (403 `chat_archived`; через 90 дней его удалит фоновая очистка). Запись в режиме `ReadOnly` — 409/исключение `chat_read_only` (с `reason`). Блокировка прерывает обмен, поэтому чат пары становится только для чтения.
- Чужой обмен и чужое сообщение — 404 `exchange_not_found` / `message_not_found` (в хабе то же в тексте исключения).
- В `GET /exchanges` и `GET /exchanges/{id}` добавлено `unreadMessages` (чужие непрочитанные, удалённые не в счёт) и `chat` (режим).

**Уведомления о сообщениях (без спама).** В интерфейсе одно уведомление `NewMessages` на обмен и получателя, пока есть непрочитанные: новые сообщения **обновляют его** (тот же `id`, `payload.count` растёт, время создания обновляется, оно поднимается в списке, по хабу `/hubs/notifications` приходит снова с тем же `id`: клиент заменяет по `id`). Когда получатель прочитал всё (`POST …/messages/read` или `Read` в хабе), оно становится прочитанным; если он закрыл уведомление вручную, следующее сообщение создаст новое. `payload`: `exchangeId`, `userId`, `userName` (автор), `count`. По email: если сообщение не прочитано **1 час**, уходит одно письмо (tg, ru, en, без текста сообщений, со ссылкой на обмен), **не чаще одного в сутки на обмен**; следующее письмо только про сообщения, пришедшие после предыдущего письма. Непрочитанное по-прежнему видно в `unreadMessages`.

## Выход после неявок партнёра (этап 5, шаг 4)

Если партнёр трижды не пришёл на урок, пострадавший может выйти из обмена без последствий для себя (раздел 6.3 ТЗ).

- В обмене (`GET /exchanges`, `GET /exchanges/{id}`) есть `partnerNoShows` (сколько **окончательных** неявок у партнёра в этом обмене) и `canAbortForNoShow` (`true`, когда обмен активен и неявок не меньше трёх). Кнопку «Выйти из-за неявок» показывать по `canAbortForNoShow`.
- **Окончательная неявка** — урок в статусе `NoShow`, итог которого подведён при закрытии комнаты (старт + 75 минут). Неявка «на 15-й минуте» не считается: опоздавший ещё может зайти, и тогда урок станет `Completed`. Свои неявки не считаются (счёт идёт по партнёру), неявки в других обменах тоже.
- Выход: `POST /exchanges/{id}/abort` с `{ "reason": "partner_no_show" }` (регистр и пробелы по краям не важны). Меньше трёх неявок: 409 `no_show_threshold_not_reached` с полями `partnerNoShows` и `required`. Любой другой текст причины — обычный выход, признака неявок он не ставит.
- Результат: `Aborted`, **виновным записан партнёр**: у вышедшего `abortedBy: "partner"`, у не пришедшего `abortedBy: "me"`; `abortReason: "partner_no_show"` и `abortedForNoShow: true` у обоих. Открытые уроки отменяются без виновного, партнёру приходит уведомление `ExchangeAborted` (`payload.reason` = `partner_no_show`, `payload.userId` — вышедший).
- Признак `abortedForNoShow` хранится постоянно: на нём этап 6 даст пострадавшему одностороннее право на отзыв (раздел 8.1 ТЗ, пункт 3).
- Нельзя выйти так из замороженного или уже закрытого обмена (409 `invalid_state_transition`), чужой обмен — 404. Два одновременных выхода дают один успешный и конфликт.

## Файлы, голосовые и аватар (этап 4, шаг 6): контракт для фронтенда

### Хранилище (для разработчика)
Файлы лежат в закрытом бакете S3. Локально это **Garage** (вместо MinIO, образы которого больше не публикуются). Один раз:

1. В `backend/.env` задайте `GARAGE_RPC_SECRET` (64 hex), `STORAGE_ACCESS_KEY` (`GK` + 24 hex), `STORAGE_SECRET_KEY` (64 hex), см. `.env.example`.
2. `docker compose up -d garage`, затем `./scripts/init-storage.ps1` (или `init-storage.sh`): раскладка, ключ, бакет. Скрипт можно запускать повторно, в конце он печатает 4 команды `dotnet user-secrets set "Storage:…"`.
3. Запустите API. При старте он сам проверяет бакет и разрешает браузеру фронтенда читать файлы (CORS: только `Frontend:BaseUrl`, только GET и HEAD).

Настройки (`Storage__…`): `Endpoint`, `PublicEndpoint` (если браузер видит хранилище по другому адресу, чем приложение; подписанные ссылки строятся на него), `AccessKey`, `SecretKey`, `Bucket`, `Region` (у Garage `garage`), `ForcePathStyle` (true), `TimeoutSeconds` (30: сколько ждать ответ хранилища) и `MaxRetries` (2: повторы SDK при сбоях; в тестах «хранилище недоступно» 2 с и 0). Неверные значения (таймаут < 1, повторы < 0) — отказ при старте. **Вне Development и Testing без хранилища приложение не запустится**; в Development без него загрузки отвечают 503 `storage_not_configured`. Переход на облачное S3 — только смена этих настроек. Если хранилище недоступно: 503 `storage_unavailable`.

### Файлы и голосовые в чате (тег **Chat**)
`POST /exchanges/{id}/attachments` — multipart/form-data: поле `file` и, для голосового, `durationSeconds`. В ответ 201 и **сообщение** (тот же объект, что в истории и в событии `message`) с типом `File` или `Voice` и полем `attachment`: `id`, `kind` (`File`, `Image`, `Voice`), `fileName`, `contentType`, `sizeBytes`, `durationSeconds`. Собеседник получает его по хабу как обычное сообщение; оно же попадает в `unreadMessages` и в уведомление `NewMessages`.

- **Белый список:** pdf, docx, xlsx, pptx, zip, txt, png, jpg (jpeg), webp до **20 МБ**; голосовое `.webm` (Opus) до **4 МБ** и до **120 секунд** (`durationSeconds` 1–120, обязателен). Тип определяет сервер **по содержимому** (сигнатуры), а не по `Content-Type` клиента; расширение должно соответствовать содержимому. Для docx/xlsx/pptx проверяется обязательная часть архива.
- **Картинки** (png, jpg, webp) перекодируются: EXIF, GPS, XMP и комментарии удаляются, поворот по EXIF применяется к пикселям; размер не меняется. Больше 25 мегапикселей не принимается.
- **Имя файла** очищается (путь, управляющие символы, `<>:"|?*`), не длиннее 100 знаков с сохранением расширения.
- **Ошибки:** 400 `file_required`, `unsupported_file_type`, `file_type_mismatch`, `file_empty`, `invalid_file_name`, `invalid_duration`, `voice_too_long`, `image_too_large`, `image_corrupt`; 413 `file_too_large`; 409 `chat_read_only`; 404 `exchange_not_found`; 429 `chat_rate_limited` (файл считается сообщением); 503 `storage_unavailable`.
- Сообщение с файлом нельзя править (409 `message_not_editable`), но можно удалить в течение 15 минут (`Delete` в хабе); после удаления вложения в истории нет, ссылка не выдаётся.

`GET /attachments/{id}/url` → `{ url, expiresAt, fileName }`. Ссылка **подписанная, действует 5 минут**, браузер сохраняет файл под его именем (`Content-Disposition: attachment`). Запрашивайте её в момент скачивания, а не при показе списка. Для голосового и картинок можно подставить `url` в `<audio>` и `<img>` сразу после запроса. Доступна обоим участникам, пока чат им доступен (в том числе только для чтения); чужое и несуществующее — 404 `attachment_not_found`; через 30 дней после закрытия обмена 403 `chat_archived`. Бакет закрыт: без подписи или с изменённой ссылкой хранилище отвечает отказом.

### Аватар (тег **Profile**)
- `PUT /me/avatar` — multipart, поле `file`: png, jpg или webp **до 2 МБ**. Сервер удаляет метаданные, **обрезает до квадрата по центру и уменьшает до 512×512** (меньшие картинки не увеличиваются), хранит WebP. Ответ `{ avatarUrl }`.
- `DELETE /me/avatar` — убрать (всегда 204).
- **`avatarUrl`** в профиле (`GET /me`, `GET /users/{id}`, карточки подбора, заявок и обменов) — **подписанная ссылка на 1 час** (абсолютный адрес хранилища, показывается в `<img src>` как есть). Открытого постоянного адреса у аватара **нет**: бакет закрыт. Ссылка выдаётся только тому, кто вправе видеть профиль: блокировавшие друг друга и (пока включена настройка) люди из разных возрастных групп вместо ссылки получают `null`; в списке заблокированных аватаров нет; свой аватар виден всегда. Если хранилище недоступно, профиль всё равно отдаётся, `avatarUrl` = `null`. Ссылка меняется при каждом запросе, поэтому держите её в состоянии компонента, а не в долгом кэше, и после часа запрашивайте профиль заново.
- Ошибки (аватар): 400 `file_required`, `unsupported_image` (не png/jpg/webp, пустой или повреждённый файл), `image_too_large`; 413 `avatar_too_large`; 503 `storage_unavailable`.

## Уроки (этап 4, шаг 7): контракт для фронтенда

Тег **Lessons**. Урок (в ТЗ «сессия», таблица `Sessions`) — 45 минут внутри обмена. Участник предлагает время **карточкой в чате**, партнёр принимает, отклоняет или предлагает другое. Все времена в UTC (ISO 8601), показывайте их в часовом поясе пользователя; на вход время можно присылать с любым смещением (`2026-11-20T18:00:00+05:00`).

| Метод | Что делает |
| --- | --- |
| `POST /exchanges/{id}/sessions` | Предложить время: `startAt`, `teacherId` (кто проводит: один из двух участников), `topic` (до 200). 201 + урок; в чат уходит карточка |
| `GET /exchanges/{id}/sessions` | Все уроки обмена, новые по началу сверху |
| `GET /sessions/{id}` | Один урок |
| `POST /sessions/{id}/confirm` | Принять (только партнёр автора). `Proposed → Scheduled` |
| `POST /sessions/{id}/decline` | Отклонить (только партнёр). `Proposed → Declined` |
| `POST /sessions/{id}/reschedule` | Тело `{ "startAt": … }`. `Scheduled → Proposed` с новым временем (отвечает тот, кто не предлагал перенос) |
| `POST /sessions/{id}/cancel` | Автор отзывает предложение; запланированный урок отменяет любой участник |
| `POST /sessions/{id}/confirm-manual` | «Урок состоялся»: только для урока `Unconfirmed` (оба нажимают в течение 24 часов после закрытия комнаты) |
| `GET /me/sessions?from=&to=&includeClosed=` | Календарь: мои уроки во всех обменах `[from, to)`, по возрастанию начала, с партнёром (`partner`: `id`, `name`, `avatarUrl`). По умолчанию ближайшие 30 дней, не больше 100 дней. Без `includeClosed` нет отклонённых, истёкших и отменённых |

Урок: `id`, `exchangeId`, `teacherId`, `startAt`, `endAt`, `durationMinutes` (45), `topic`, `status`, `proposedById` (кто предложил текущее время: ответить может другой), `previousStartAt` (при переносе), `cancelledById` (null — отмена без виновного), `confirmedBy` (кто нажал «Урок состоялся»), `unconfirmedAt`, `confirmDeadlineAt`, `createdAt`.

**Статусы** (раздел 9.3): `Proposed`, `Scheduled`, `Declined`, `Expired`, `Cancelled`, `LateCancelled`, `InProgress` (кто-то вошёл в видеокомнату), `NoShow`, `Missed`, `Completed`, `Unconfirmed`, `NotCounted`. В уроке также `noShowUserId` (кто не пришёл) и `lateJoinMinutes` (на сколько минут опоздал второй участник, если урок всё же засчитан).

**Правила времени:**
- Время начала хранится **с точностью до миллисекунды** (лишние знаки отбрасываются, проверка «не раньше чем через час» идёт по присланному значению). Так оно без потерь проходит через базу и через клиентов, а фоновые задачи (проверка на 15-й минуте, закрытие комнаты, напоминания) находят урок по тому же значению. Значение в ответе может отличаться от присланного не больше чем на миллисекунду.
- Слот не раньше чем через **1 час** от сейчас (400 `slot_too_soon`) и не позже `endDate` обмена (400 `slot_after_exchange_end`); граничные значения допустимы.
- Предложение без ответа **истекает в момент начала** (`Expired`, автору уведомление `SessionExpired`). Если принять после начала: 409 `session_expired`.
- **Перенос**: только запланированный урок и не позднее чем за **2 часа** до начала (409 `reschedule_too_late`, дальше только отмена); старое время сразу свободно, новое подчиняется тем же правилам и ждёт подтверждения партнёра.
- **Отмена** запланированного урока: за 2 часа и раньше `Cancelled`, позже `LateCancelled` (учитывается модератором); после начала нельзя (409 `session_already_started`). Предложение отзывает только автор (иначе 403 `not_session_proposer`).
- **Пересечения** (409 `slot_overlap` с `conflictFor`: `you` — занят я, `partner` — занят собеседник): проверяются при принятии у **обоих** участников по **всем** их обменам, под блокировкой обоих, поэтому два одновременных подтверждения не поставят человека на два урока. Занятость — подтверждённые уроки; уроки встык (конец = начало) не пересекаются. Предлагающий не может предложить время, на которое сам занят.
- Не больше 5 предложений без ответа в обмене (409 `too_many_proposals`). Менять уроки можно только в активном обмене (409 `exchange_not_active`; предложить в закрытом чате: 409 `chat_read_only`).
- Ответить на своё предложение нельзя (403 `not_session_answerer`). Чужой урок — 404 `session_not_found`.

**Карточка в чате.** Сообщение типа `SessionCard` с полем `session` (актуальный урок). После любого изменения урока оба участника получают в хабе `/hubs/chat` событие **`sessionUpdated`** с уроком целиком: замените состояние карточки по `session.id`. Карточку нельзя править и удалять. Новая карточка даёт обычное уведомление `NewMessages`; перенос (новое время без новой карточки) даёт уведомление `SessionProposed`.

**Напоминания:** за **24 часа** и за **15 минут** до начала подтверждённого урока, обоим: уведомление `SessionReminder` и **письмо** (tg, ru, en) с временем урока в часовом поясе получателя и названием пояса. Если урок подтверждён меньше чем за сутки, приходит только 15-минутное. После переноса напоминания ставятся заново, об отменённом уроке не приходят.

**Обмен закрывается** (прерван, заблокирован, завершён; на этапе 6 и заморожен): открытые уроки (`Proposed`, `Scheduled`) становятся `Cancelled` **без виновного** (`cancelledById: null`), карточки обновляются событием; отдельных уведомлений нет. Неподтверждённые и засчитанные уроки не трогаются, их ещё можно подтвердить.

**Факт урока фиксируется автоматически по присутствию в видеокомнате** (раздел 6.3 ТЗ; фоновые задачи ставятся при подтверждении слота и переставляются при переносе):

| Момент | Что видно в комнате | Статус |
| --- | --- | --- |
| Первый вход (в любой момент окна комнаты) | кто-то вошёл | `Scheduled → InProgress` |
| Старт + 15 минут (предварительно) | никого | `Missed` (уведомление `SessionMissed` обоим) |
| | один | `NoShow`, `noShowUserId` = не вошедший (уведомление `SessionNoShow` обоим, `payload.userId` — не пришедший) |
| | оба | остаётся `InProgress` |
| Старт + 75 минут, закрытие комнаты (окончательно) | вместе ≥ 30 минут | `Completed` из `InProgress`, `NoShow` или `Missed` (`SessionCompleted`); опоздание в `lateJoinMinutes` |
| | оба были, вместе < 30 минут | `Unconfirmed` (`SessionNeedsConfirmation`) |
| | один или никого (если 15-я минута не отработала) | `NoShow` / `Missed` |

«Был в комнате на 15-й минуте» значит: находился в комнате в промежутке от начала урока до 15-й минуты (вход раньше начала считается, если участник остался). **Совместное время** считается как пересечение интервалов обоих (с переподключениями) **от планового начала до закрытия комнаты**: минуты до начала урока не засчитываются. `NoShow` и `Missed` после закрытия комнаты окончательные (`Missed` меняется только на `Completed`). При закрытии комнаты бэкенд удаляет её в LiveKit (участников отключает); итог урока от доступности LiveKit не зависит (удаление повторяется само).

**Ручное подтверждение — только для `Unconfirmed`** (обрыв связи): оба нажимают `confirm-manual` («Урок состоялся») в течение **24 часов с закрытия комнаты** → `Completed` (`SessionCompleted`), иначе `NotCounted` (`SessionNotCounted`). Для остальных статусов `confirm-manual` отвечает 409 `invalid_state_transition`; после срока 409 `confirm_window_closed`. Повторное нажатие не ошибка (`confirmedBy` показывает, кто уже нажал).

**Баланс** в карточке обмена (`balance`): `myLessons` и `partnerLessons` — число **засчитанных (`Completed`)** уроков, проведённых каждым в роли преподавателя (`teacherId`); `showReminder` при разнице больше 3. `NotCounted`, отменённые и несостоявшиеся не считаются.

Типы уведомлений (`payload`: `sessionId`, `exchangeId`, `startAt`, `userId`, `userName`, у переноса `previousStartAt`, у отмены `late`, у напоминания `minutesBefore`): `SessionProposed` (перенос, партнёру), `SessionConfirmed` и `SessionDeclined` (автору), `SessionCancelled` (партнёру отменившего), `SessionExpired` (автору), `SessionReminder` (обоим, дублируется письмом), `SessionNeedsConfirmation`, `SessionCompleted`, `SessionNotCounted`, `SessionNoShow`, `SessionMissed` (обоим).

## Удаление аккаунта и выгрузка данных (этап 4, шаг 8): контракт для фронтенда

Тег **Account**, нужен вход.

### `DELETE /me`
Тело: `{ "password": "…", "code": "…", "isRecoveryCode": false }`. Пароль обязателен; `code` (код приложения или резервный код с `isRecoveryCode: true`) обязателен, если у пользователя включена 2FA. Ответ 204, cookie с refresh-токеном очищается, после этого переведите пользователя на страницу прощания и сбросьте состояние входа.

Ошибки: 400 `invalid_password`, `two_factor_code_required`, `invalid_two_factor_code`, `validation_failed`; 403 `staff_cannot_delete_account` (служебные аккаунты так не удаляются); 429 `account_locked_out` (неверные пароли считаются, как при входе: 5 ошибок — блокировка на 15 минут).

Что происходит в одной транзакции:
- **Активные и замороженные обмены** → `Aborted` без виновного. Партнёр видит `abortReason: "partner_unavailable"` (то же нейтральное значение, что при блокировке), `abortedBy: null` и получает уведомление `ExchangeAborted` без имени. Открытые уроки обмена (`Proposed`, `Scheduled`) → `Cancelled` без виновного, карточки в чате обновляются.
- **Ожидающие заявки** в обе стороны → `Expired` (без уведомлений). Блокировки с его участием и все его уведомления удаляются.
- **Личные данные стираются:** имя (заменяется подписью), «о себе», дата рождения, языки занятий, навыки, аватар (из хранилища). Видимость в подборе выключена навсегда.
- **Email и логин** заменяются обезличенными (`deleted-<id>@deleted.skillswap.invalid`): прежний адрес **можно зарегистрировать заново**. Пароль, 2FA и резервные коды убираются, **все сеансы отзываются**; статус `Deleted`. Его access-токен ещё до 15 минут живёт, но `GET /me` и другие методы отвечают 403 `account_not_active`.
- **Чужие данные:** его сообщения остаются у партнёров до обычной очистки чатов (через 90 дней), у них та же `senderId`. Везде, где показывается пользователь (`partner`, `fromUser`, `toUser` в обменах, заявках, календаре), у удалённого `isDeleted: true`, имя «Удалённый пользователь» (**переводите по флагу `isDeleted`, а не по тексту**), `avatarUrl: null`. В чужих уведомлениях его имя в `payload.userName` заменено той же подписью. Отзывы (когда они появятся) останутся с этой подписью (раздел 8.1).
- **Открытое обращение** (этап 6): если оно есть, личные данные (имя, о себе, дата рождения, навыки) хранятся до его закрытия, остальное выполняется сразу (`ErasurePending`); при закрытии обращения вызывается `AccountDeletionService.CompleteErasureAsync`.
- На **старый адрес** уходит письмо «аккаунт удалён» (tg, ru, en).

### `POST /me/export` и `GET /me/export`
`POST` ставит фоновую задачу и отвечает **202** `{ id, status, requestedAt, readyAt, expiresAt }` (ссылки в ответе нет). Задача собирает JSON: аккаунт (email, имя, о себе, дата рождения, язык, пояс), языки занятий, навыки, заявки, обмены (партнёр только по имени), уроки, **свои** сообщения **расшифрованные** (удалённые помечены `deletedAt`), блокировки, отзывы (`reviewsWritten`, `reviewsReceived`; пока пусты, появятся с отзывами). Чужих сообщений и чужих личных данных, пароля, ключей и токенов в файле нет. Готовый файл кладётся в закрытый бакет, на email приходит письмо (tg, ru, en) со **ссылкой на скачивание на 24 часа** (подписанная, `Content-Disposition: attachment`), потом файл удаляется.

**Не чаще одного раза в сутки:** повторный `POST` — 429 `export_rate_limited`. Неудавшаяся выгрузка (`Failed`) не мешает повторить сразу. `GET /me/export` — состояние последней: `Pending`, `Processing`, `Ready` (с `expiresAt`), `Failed`, `Expired`; не запрашивали — 404 `export_not_found`. Служебным аккаунтам 403 `regular_account_required`.

## Главная страница: `GET /me/dashboard` (этап 4, шаг 9)

Тег **Profile**, нужен вход, только обычный аккаунт. Один запрос вместо пяти; все даты в UTC.

- **`nextSession`** — ближайший подтверждённый урок (`Scheduled` или идущий `InProgress`, которому ещё не вышло время окончания) по всем обменам, иначе `null`. Поля: `id`, `exchangeId`, `startAt`, `endAt`, `durationMin`, `topic`, `status`, `iAmTeacher`, `partner` (`id`, `name`, `avatarUrl` с теми же правилами видимости, что везде, `isDeleted`), `canJoinFrom` (за 10 минут до начала) и `canJoinUntil` (через 30 минут после планового окончания). Видеокомната появится на этапе 5.
- **`counters`**: `activeExchanges` (активные и замороженные), `incomingRequests` и `outgoingRequests` (ожидающие, срок не вышел), `unreadMessages` (чужие, не удалённые, в доступных для чтения чатах: активные, замороженные и закрытые не больше 30 дней назад), `unreadNotifications`.
- **`stats`**: `completedSessions` (`Completed` во всех обменах), `taughtMinutes` (уроки, где преподавал я), `learnedMinutes` (где преподавал партнёр), `completedExchanges` (только `Completed`, прерванные не считаются). `NotCounted` и отменённые не учитываются.
- **`rating`**: `average` (у «Новичка» `null`, как в профиле), `count`, `isNewcomer`.
- **`actionsNeeded`** — что ждёт ответа, **самое срочное сверху** (по сроку `dueAt`), каждого вида не больше 20. Типы: `AnswerRequest` (`requestId`, `dueAt` — срок ответа), `AnswerSession` (партнёр предложил время: `sessionId`, `exchangeId`, `dueAt` — начало урока), `ConfirmSession` (урок закончился, я не нажал «Урок состоялся»: `sessionId`, `exchangeId`, `dueAt` — до какого времени можно), `FinishExchange` (партнёр нажал «Завершить», а я нет: `exchangeId`), `AnswerExtension` (партнёр согласился продлить: `exchangeId`). Уже сделанное, истёкшее и своё предложение (ответ ждёт партнёр) в списке не показываются. `WriteReview` (обмен закрыт, есть право на отзыв, отзыва ещё нет: `exchangeId`, `dueAt` — срок отзыва).


## Видеокомната (этап 5, шаг 1): токен входа

Тег **Lessons**. `POST /sessions/{id}/join-token` без тела. Нужен вход, только обычный аккаунт; чужой урок — 404 `session_not_found`.

Ответ 200 (не кэшируется): `token` (короткий токен LiveKit), `url` (WebSocket медиасервера), `roomName` (`session-{id урока}`), `expiresAt` (токен годится для **входа** до этого момента; после входа сеанс от него не зависит, поэтому при переподключении после обрыва запросите новый), `startAt`, `plannedEndAt` (старт + 45 минут), `roomClosesAt` (старт + 75 минут).

**Если связь оборвалась и токен уже истёк**, фронтенд просто запрашивает новый `join-token` (пока окно открыто, см. ниже) и переподключается; повторный вход — обычное дело, присутствие хранится несколькими интервалами.

**Таймер «осталось 5 минут» — на фронтенде**: показать уведомление в `plannedEndAt − 5 минут` (40-я минута урока). Бэкенд для этого ничего не шлёт, фоновой задачи нет.

Окно входа: от **старта − 10 минут** до **старта + 75 минут** (момент закрытия уже закрыт). Токен выдаётся для уроков `Scheduled`, `InProgress`, а также `NoShow` и `Missed` (опоздавший может зайти). Коды 409:

| Код | Когда | Доп. поля |
| --- | --- | --- |
| `join_window_not_open` | рано | `opensAt` |
| `join_window_closed` | поздно | `closedAt` |
| `session_not_joinable` | урок не подтверждён или уже закрыт (`Proposed`, `Completed`, `Unconfirmed` …) | `sessionStatus` |
| `exchange_frozen` | обмен заморожен | |
| `exchange_not_active` | обмен завершён или прерван | |

503 `livekit_not_configured` — видеосервер не настроен. Права токена: войти в свою комнату, публиковать и слушать; управлять и удалять комнату нельзя. Присутствие, статусы `InProgress`/`NoShow`/`Missed` и итог урока появятся в следующих шагах этапа 5.

## Присутствие в комнате (этап 5, шаг 2): для бэкенда

Фронтенду здесь ничего вызывать не нужно: присутствие собирается из событий LiveKit (`participant_joined`, `participant_left`, `room_finished`) автоматически. Видно это по статусу урока: первый вошедший в комнату переводит подтверждённый урок `Scheduled → InProgress` (обоим уходит `sessionUpdated` в хабе чата).

- Каждое событие сначала записывается в журнал `LiveKitWebhookEvents` один раз по `EventId`, затем учитывается. Интервалы (`SessionAttendances`: `JoinedAt`, `LeftAt`) **каждый раз строятся заново по журналу комнаты**, а не двигаются событиями. Поэтому повторы, перестановка событий и потерянное событие не портят картину, а обработку можно безопасно повторить.
- Переподключения: у участника несколько интервалов, но **открытый** не больше одного (уникальный индекс). Повторный вход без выхода игнорируется, выход без входа тоже. `room_finished` закрывает открытые интервалы обоих.
- События с одинаковым временем (LiveKit пишет секунды) идут по порядку приёма, а при совсем равных вход считается раньше выхода.
- Учитываются только участники урока (идентификатор в токене = `userId`) и только подтверждённый и ещё не закрытый урок (`Scheduled`, `InProgress`, `NoShow`, `Missed`). События чужих комнат, чужих людей и закрытых уроков отмечаются обработанными без последствий.
- Если обработка упала, webhook отвечает ошибкой и LiveKit повторяет доставку: повтор уже записанного, но необработанного события доводит учёт до конца (`ProcessedAt` пусто — значит не учтено). Обработанное повторно не применяется.
- Всё идёт в транзакции под блокировкой обоих участников, как и другие изменения урока: два одновременных входа дают два интервала и один переход в `InProgress`.
## Видеокомната: сводный контракт для фронтенда (этап 5, шаг 5)

Всё, что нужно фронтенду для урока с видео, в одном месте. Подробности по каждому пункту выше: «Уроки», «Видеокомната: токен входа», «Присутствие в комнате», «Выход после неявок партнёра».

**Как открыть комнату.**
1. Урок `Scheduled` (подтверждён), окно: от старта − 10 минут до старта + 75 минут. Кнопка «Войти» нужна, пока `now ∈ [startAt − 10 мин, startAt + 75 мин)`; эти границы есть в `GET /me/dashboard` (`canJoinFrom`, `canJoinUntil` у ближайшего урока) и в ответе токена (`roomClosesAt`).
2. `POST /sessions/{id}/join-token` → `{ token, url, roomName, expiresAt, startAt, plannedEndAt, roomClosesAt }`. Подключиться клиентом LiveKit: `room.connect(url, token)`. Обрыв связи и токен уже истёк — запросить токен заново (пока окно открыто) и переподключиться.
3. **`url` берётся из настроек бэкенда** (`LiveKit:Url`), фронтенд его не конструирует. В Development это `ws://localhost:7880` (задан в `appsettings.Development.json`), на боевом сервере — `wss://…`.
4. «Осталось 5 минут» на 40-й минуте показывает фронтенд сам по таймеру от `plannedEndAt − 5 минут`.

**Что видеть в интерфейсе.**
- Статус урока и кто в комнате приходят событием `sessionUpdated` в хабе `/hubs/chat` (см. `docs/API-ANSWERS.md`, вопрос 2): поля `status`, `participantsInRoom` (id пользователей, которые сейчас в комнате; по нему «партнёр в комнате»), `noShowUserId`, `lateJoinMinutes`. То же поле есть в `GET /sessions/{id}` и `GET /exchanges/{id}/sessions`. В карточках внутри истории чата и в календаре оно пустое: актуальное значение приходит событием.
- `InProgress` — кто-то вошёл. `NoShow` / `Missed` на 15-й минуте — предварительно (опоздавший ещё может зайти). Итог при закрытии комнаты: `Completed`, `Unconfirmed` (оба были, но вместе меньше 30 минут: кнопка «Урок состоялся» в течение 24 часов), окончательный `NoShow` или `Missed`.
- Вход невозможен: 409 с кодами `join_window_not_open` (`opensAt`), `join_window_closed` (`closedAt`), `session_not_joinable` (`sessionStatus`), `exchange_frozen`, `exchange_not_active`; 503 `livekit_not_configured`.
- Три окончательные неявки партнёра: в обмене `partnerNoShows` и `canAbortForNoShow`, выход — `POST /exchanges/{id}/abort` с `reason: "partner_no_show"`.
- Список чатов: в карточке обмена `lastMessage` (см. ниже).

**Схема OpenAPI.** Все перечисления (статусы, типы, `AttachmentKind`, `DataExportStatus` и остальные) описаны как `type: string` со списком значений и приходят в JSON строками. Тест `OpenApiSnapshotTests` проверяет, что integer-перечислений в схеме нет.

**`lastMessage` в карточке обмена** (`GET /exchanges`, `GET /exchanges/{id}`), для списка чатов: `{ id, type, preview, createdAt, senderId, isDeleted }` или `null`, если в чате нет сообщений. `preview` — начало текста, до 100 символов (длиннее: 99 знаков и «…»; переводы строк и повторные пробелы сжаты в один пробел; эмодзи не разрываются). Для голосового, файла, карточки урока и удалённого сообщения `preview` = `null`: подпись по `type` строит фронтенд, у удалённого `isDeleted: true`. Текст хранится зашифрованным и расшифровывается при выдаче.

**CORS.** Разрешён один адрес `Frontend:BaseUrl` (локально `http://localhost:5173`, косая черта в конце не важна) с `credentials` для REST (включая refresh-cookie) и для SignalR (`/hubs/chat`, `/hubs/notifications`: согласование соединения и предварительные запросы, заголовки `authorization`, `x-signalr-user-agent`, `content-type`). Ответы об ошибках (401, 409…) тоже несут CORS-заголовки, поэтому фронтенд видит код ошибки, а не «CORS-ошибку». Другой адрес (другой порт, `https`, `null`) разрешения не получает. WebSocket в браузере не умеет ставить заголовок `Authorization`: токен передаётся параметром `access_token` в адресе. Проверено автотестами (`CorsTests`).

### Ручная проверка видеокомнаты без ожидания часов (только Development)

1. Поднять контейнеры (`docker compose up -d`, включая `livekit`), бэкенд запустить в профиле `http` (`dotnet run --launch-profile http`), фронтенд на `http://localhost:5173` с настоящим бэкендом (`VITE_USE_MOCKS=false`).
2. **Сроки урока сокращены** в `backend/src/SkillSwap.Api/appsettings.Development.json` (секция `Lessons`, значения в минутах; файл в репозитории, секретов в нём нет):

   | Настройка | ТЗ | Development | Что значит |
   | --- | --- | --- | --- |
   | `MinLeadMinutes` | 60 | 1 | слот не раньше чем через столько минут |
   | `FreeChangeMinutes` | 120 | 1 | отмена и перенос без последствий |
   | `JoinOpensBeforeMinutes` | 10 | 3 | вход открывается за столько минут до начала |
   | `FirstCheckMinutes` | 15 | 2 | проверка «пришли ли оба» после начала |
   | `JoinClosesAfterEndMinutes` | 30 | 3 | комната закрывается после окончания |
   | `MinJointMinutes` | 30 | 2 | сколько вместе, чтобы урок засчитался |
   | `DurationMinutes` | 45 | 5 | длительность урока |
   | `ManualConfirmMinutes` | 1440 | 60 | срок ручного подтверждения `Unconfirmed` |

   Приложение применяет их при старте и **принимает значения, отличные от ТЗ, только в Development и Testing**: в боевой среде такие настройки не дадут запуститься. Бессмысленные наборы (например, зачёт дольше самого урока) тоже останавливают запуск. Напоминания за 24 часа и 15 минут не менялись. Новые настройки читаются при перезапуске бэкенда.
3. **`POST /dev/seed-lesson`** (без тела, без входа; маршрута нет вне Development, в схему и Scalar не попадает): создаёт двух подтверждённых пользователей с полными анкетами (преподаватель и ученик), обмен и подтверждённый урок, **который начинается через 2 минуты**. Всё идёт через настоящие сервисы (заявка → принятие → предложение → подтверждение), поэтому ставятся и настоящие фоновые задачи урока (проверка на 2-й минуте, закрытие комнаты). Ответ (не кэшируется, в нём пароли):

   ```json
   {
     "exchangeId": "…", "sessionId": "…", "roomName": "session-…",
     "startAt": "…Z", "endAt": "…Z", "joinOpensAt": "…Z", "firstCheckAt": "…Z", "roomClosesAt": "…Z",
     "users": [
       { "role": "teacher", "userId": "…", "email": "dev-teacher-1a2b3c4d@example.com", "password": "…", "name": "Преподаватель (dev)" },
       { "role": "student", "userId": "…", "email": "dev-student-1a2b3c4d@example.com", "password": "…", "name": "Ученик (dev)" }
     ],
     "exchangeUrl": "http://localhost:5173/exchanges/…",
     "hint": "…"
   }
   ```

   Каждый вызов создаёт новых пользователей и новый урок. Если сроки не сокращены (`MinLeadMinutes` ≥ 2), ответ 409 `lesson_timing_not_shortened` с подсказкой, что менять.
4. Войти двумя пользователями в **разных браузерах** (или обычное окно и инкогнито), открыть `exchangeUrl`. Вход открывается за 3 минуты до начала, то есть сразу. Проверить: «партнёр в комнате», звук и видео, обрыв и возврат, закрытие комнаты через 3 минуты после окончания (≈ 10 минут после запуска), итог урока. Сценарии: зайти оба на минуту (`Unconfirmed`), зайти оба на 2+ минуты (`Completed`), зайти одному (`NoShow`), не заходить (`Missed`).

### Проверка видеозвонка с телефона в одной сети

Локальный LiveKit объявляет адрес `127.0.0.1` (`node_ip` в `backend/livekit.yaml`), поэтому звонок по умолчанию работает только с этого компьютера. Чтобы подключить телефон (адрес компьютера в сети, например `192.168.1.50`, узнать: `ipconfig`):

1. **`backend/livekit.yaml`:** заменить `node_ip: 127.0.0.1` на LAN-адрес компьютера; перезапустить контейнер (`docker compose restart livekit`).
2. **Бэкенд:** `LiveKit:Url` = `ws://192.168.1.50:7880` (user-secrets или переменная `LiveKit__Url`; именно этот адрес бэкенд отдаст в `join-token`), `Frontend:BaseUrl` = `http://192.168.1.50:5173` (иначе CORS отклонит запросы), и слушать не только localhost: `dotnet run --launch-profile http --urls http://0.0.0.0:5023`. Webhook LiveKit → API идёт по `host.docker.internal`, менять его не нужно.
3. **Фронтенд:** `npm run dev -- --host`, в `.env` `VITE_API_URL=http://192.168.1.50:5023/api/v1`.
4. **Брандмауэр Windows:** разрешить входящие TCP 5023, 5173, 7880, 7881 и UDP 7882.
5. **Камера и микрофон на телефоне требуют защищённого адреса (HTTPS)**: на `http://192.168.1.50:5173` браузер не даст доступ к ним (исключение только у `localhost`). Варианты: туннель с HTTPS к фронтенду (например `cloudflared tunnel --url http://localhost:5173`; тогда `Frontend:BaseUrl` и `VITE_API_URL` тоже на HTTPS-адреса), либо в Chrome на телефоне флаг `chrome://flags/#unsafely-treat-insecure-origin-as-secure` с адресом `http://192.168.1.50:5173`. Мобильные операторы и TURN на 443/TLS проверяются на боевом VPS (`docs/DEFERRED.md`).

### Проверка с другого устройства по HTTPS (только Development)

Камеру, микрофон и `Secure`-cookie (refresh-токен) браузер разрешает только на `https` или `localhost`. Поэтому для проверки
со второго ноутбука в той же сети фронтенд работает по HTTPS с самоподписанным сертификатом, а всё, что видит браузер,
идёт через тот же адрес `https://<IP>:5173`: API и хабы как раньше, сигнальный канал LiveKit (`/livekit` → `localhost:7880`,
WebSocket) и файлы хранилища (`/skillswap/…` → `localhost:3900`). Бэкенд, Garage и сигнальный порт LiveKit наружу не открываются.
Медиа LiveKit идёт напрямую по UDP 7882 (TCP 7881 запасной), поэтому в `livekit.yaml` нужен LAN-адрес.

1. **LAN-адрес Mac:** `ipconfig getifaddr en0` (ниже `192.168.0.63`).
2. **Сертификат** (вне репозитория, на год, адреса в SAN):
   ```
   mkdir -p ~/.skillswap/dev-https && cd ~/.skillswap/dev-https
   cat > san.cnf <<'CNF'
   [req]
   distinguished_name = dn
   x509_extensions = v3
   prompt = no
   [dn]
   CN = SkillSwap dev
   [v3]
   basicConstraints = critical,CA:FALSE
   keyUsage = critical,digitalSignature,keyEncipherment
   extendedKeyUsage = serverAuth
   subjectAltName = IP:192.168.0.63,IP:127.0.0.1,DNS:localhost
   CNF
   openssl req -x509 -newkey rsa:2048 -sha256 -days 365 -nodes -config san.cnf -keyout key.pem -out cert.pem
   ```
3. **Фронтенд** (`frontend/.env`): `DEV_HTTPS_KEY=<путь>/key.pem`, `DEV_HTTPS_CERT=<путь>/cert.pem`. Запуск:
   `npx vite --config vite.config.ts --host 0.0.0.0`. Без этих переменных dev-сервер работает по http, как раньше.
4. **Бэкенд** (user-secrets, только Development):
   ```
   dotnet user-secrets set "Frontend:BaseUrl" "https://192.168.0.63:5173"        # ссылки в письмах, CORS
   dotnet user-secrets set "Storage:PublicEndpoint" "https://192.168.0.63:5173"  # подписанные ссылки на файлы
   dotnet user-secrets set "LiveKit:Url" "wss://192.168.0.63:5173/livekit"       # адрес, который получает браузер
   dotnet user-secrets set "LiveKit:ApiUrl" "http://localhost:7880"              # сам бэкенд ходит в LiveKit напрямую
   ```
   `LiveKit:ApiUrl` обязателен: иначе бэкенд вызывал бы API LiveKit по `LiveKit:Url` и упёрся бы в самоподписанный сертификат.
   Подписанные ссылки хранилища считаются для адреса браузера, поэтому прокси Vite не меняет у них `Host`.
5. **LiveKit:** `node_ip: 192.168.0.63` в `backend/livekit.yaml`, затем `docker compose restart livekit`. **Не коммитить**,
   вернуть: `git checkout backend/livekit.yaml`.
6. **Файрвол macOS** (если включён): входящие для `node` (TCP 5173) и Docker (UDP 7882, TCP 7881).
7. **Браузер:** открыть `https://192.168.0.63:5173` на обоих ноутбуках (и на Mac тоже, а не `localhost`: CORS и подписанные
   ссылки настроены на этот адрес). Предупреждение о сертификате: Chrome — «Дополнительные» → «Перейти на сайт»
   (или набрать `thisisunsafe` на странице предупреждения); Safari — «Показать подробности» → «посетить этот веб-сайт»;
   Firefox — «Дополнительно» → «Принять риск и продолжить».

Вернуться к обычной локальной работе: убрать `DEV_HTTPS_*` из `frontend/.env`, удалить четыре user-secrets выше
(`dotnet user-secrets remove …`), вернуть `livekit.yaml`.

## Отзывы (этап 6, шаг 1): контракт для фронтенда

Тег **Reviews**. Отзывы слепые и взаимные (раздел 8.1 ТЗ). Только обычные аккаунты (служебному 403 `regular_account_required`), чужой обмен 404 `exchange_not_found`.

| Метод | Что делает |
| --- | --- |
| `GET /exchanges/{id}/reviews` | Что я могу и что есть по обмену (см. ниже) |
| `POST /exchanges/{id}/reviews` | Оставить отзыв. 201 и мой отзыв |
| `PUT /exchanges/{id}/reviews` | Изменить свой отзыв, пока он не опубликован |
| `GET /users/{id}/reviews?page=&pageSize=` | Опубликованные отзывы о человеке и его рейтинг (профиль) |

**Тело отзыва:** `scoreKnowledge`, `scoreExplaining`, `scorePunctuality`, `scorePoliteness` (каждая 1–5: знание предмета, умение объяснять, пунктуальность, вежливость) и `text` (20–1000 символов, пробелы по краям не считаются). Ошибки 400: `review_score_out_of_range`, `review_text_too_short`, `review_text_too_long`.

**Когда можно оставить отзыв.** Обмен должен быть закрыт (завершён или прерван, в том числе блокировкой: заблокировать партнёра первым и этим избежать плохого отзыва нельзя) и:
1. была хотя бы **одна засчитанная (`Completed`) сессия** — отзыв доступен **обоим**;
2. либо сессий не было, но у партнёра **три окончательные неявки** — отзыв доступен только **пострадавшему** (если у партнёра тоже три неявки против меня, право у обоих);
3. иначе (нет сессий и нет неявок) отзывы недоступны.
Срок — **14 дней после закрытия обмена**. Отзыв об удалившем аккаунт не пишется. Отметка «Полный обмен» (`isFullExchange`): обмен завершён штатно и каждый провёл не меньше трёх засчитанных уроков преподавателем.

**`GET /exchanges/{id}/reviews`:** `canWrite`; если нельзя, `unavailableReason`: `exchange_not_closed`, `partner_deleted`, `no_counted_sessions`, `review_window_closed`, `review_already_exists`; `deadlineAt` (закрытие + 14 дней); `isFullExchange` (получит ли мой отзыв отметку); `oneSided` (право только у меня: отзыв опубликуется сразу); `canEdit`; `myReview` (мой отзыв виден мне всегда); `reviewAboutMe` (отзыв партнёра обо мне, **только после публикации**: слепые отзывы, до этого `null` и факт написания не раскрывается).

**Слепая публикация.** Написанный отзыв скрыт (`status: "Pending"`, `publishAt` — когда опубликуется сам). Он становится виден **одновременно с отзывом партнёра**, когда написаны оба, либо через **7 дней после первого**. Исключение — одностороннее право (`oneSided`): отзыв публикуется сразу, писать больше некому. Срок публикации при правке не сдвигается. После публикации править нельзя (409 `review_already_published`); правка после срока 14 дней: 409 `review_window_closed`; отзыва нет: 404 `review_not_found`.

**Отзыв (`ReviewDto`):** `id`, `exchangeId`, `author` (`id`, `name`, `avatarUrl`, `isDeleted`), `targetId`, четыре оценки, `average` (среднее из четырёх: столько весит отзыв), `text`, `isFullExchange`, `status` (`Pending`, `Published`, `Hidden`, `Frozen`: автору видна настоящая, адресату оспариваемый отзыв показывается как `Published`), `createdAt`, `publishedAt`, `publishAt`.

**Рейтинг** (`ratingAvg` и `ratingCount` в профиле, подборе и на главной): среднее из средних оценок всех **опубликованных и не оспариваемых** отзывов, пересчитывается при публикации (и при скрытии/возврате отзыва на следующих шагах). Меньше 3 отзывов — «Новичок»: `ratingAvg` равен `null`, `isNewcomer: true`. В отзывах удалённого автора подпись «Удалённый пользователь» (`author.isDeleted`), они продолжают влиять на рейтинг.

**`GET /users/{id}/reviews`:** `items` (публичные отзывы без служебных полей: `id`, `author`, оценки, `average`, `text`, `isFullExchange`, `publishedAt`), `page`, `pageSize` (до 50, иначе 400 `invalid_paging`), `totalCount`, `ratingAvg`, `ratingCount`, `isNewcomer`. Новые сверху; скрытые модератором и ещё не опубликованные не показываются. Видят те же люди, что и профиль: скрытый из подбора, заблокированный, удалённый пользователь, блокировка в любую сторону и другая возрастная группа дают 404 `user_not_found`.

**Уведомления** (тип, данные): `ReviewAvailable` (обмен закрыт, можно написать; `exchangeId`, `userId` и `userName` партнёра, `deadlineAt`: приходит один раз тем, у кого есть право и кто ещё не написал), `ReviewReminder` (за 2 дня до срока, тем, кто ещё не написал), `ReviewPublished` (о вас опубликован отзыв; `exchangeId`, `reviewId`, `userId` и `userName` автора). На главной (`GET /me/dashboard`) в `actionsNeeded` есть действие **`WriteReview`** (`exchangeId`, `dueAt` — срок отзыва) до тех пор, пока отзыв не написан.

**Для бэкенда.** Право на отзыв считается заново на каждый запрос (`ReviewRules`), запись идёт в транзакции под блокировкой обоих участников (два одновременных отзыва партнёров публикуются вместе, повторный отзыв автора невозможен, `UNIQUE (ExchangeId, AuthorId)`). Публикацию через 7 дней делает задача `ReviewJobs.PublishDueAsync` (срок хранится в `Reviews.PublishDueAt`), приглашение и напоминание — `InviteAsync` и `RemindAsync` (ставятся при закрытии обмена через `ReviewScheduler`: завершение, выход, блокировка; отметки `ReviewInvitesSentAt` и `ReviewReminderSentAt` не дают разослать дважды). Выгрузка данных содержит `reviewsWritten` (все свои, в том числе ещё не опубликованные) и `reviewsReceived` (только опубликованные).

## Обращения (этап 6, шаг 2): подача и отзыв, контракт для фронтенда

Тег **Cases**. Раздел 8.2 ТЗ. Только обычные аккаунты (служебному 403 `regular_account_required`), чужой обмен 404 `exchange_not_found`, партнёр удалил аккаунт 409 `partner_deleted`.

| Метод | Что делает |
| --- | --- |
| `POST /cases` | Подать обращение. 201 и обращение |
| `GET /me/cases?role=&page=&pageSize=` | Мои обращения, новые сверху. `role`: `All` (по умолчанию), `Opened` (подал я), `Against` (поданы на меня) |
| `GET /cases/{id}` | Обращение |
| `POST /cases/{id}/withdraw` | Отозвать, пока не взято в работу |

**Тело `POST /cases`:** `type`, `category`, `exchangeId`, `reviewId` (только для спора), `description` (20–2000 символов), `messageIds` (до 20 сообщений этого обмена), `fromCall` (по умолчанию `false`).

| `type` | `category` | Срок подачи | Приоритет |
| --- | --- | --- | --- |
| `ReviewDispute` (спор по отзыву) | `ReviewUnfair`, `ReviewAbusive`, `ReviewFalse` | 14 дней после публикации отзыва | обычный |
| `Conduct` (поведение) | `Insults`, `Threats`, `Harassment`, `Spam`, `Extortion` | 90 дней после закрытия обмена (пока идёт обмен — всегда) | высокий |
| `NoShowAbuse` (неявки и срыв) | `SystematicEvasion`, `BadFaithLessons` | 30 дней после закрытия обмена | обычный |

Приоритет высокий также при `fromCall` и если один из двоих младше 18. Срок реакции: 24 часа (высокий) или 72 часа (обычный), хранится в `Cases.DueAt`.

**Спор по отзыву.** Подаёт адресат отзыва; отзыв должен быть опубликован (скрытый слепой публикацией или скрытый модератором выглядит как несуществующий: 404 `review_not_found`). Отзыв сразу замораживается (`Frozen`) и перестаёт влиять на рейтинг, адресат и автор видят его как опубликованный. Один отзыв: один спор (409 `review_already_disputed`, пока идёт; 409 `case_already_exists`, если спор уже решён). Отзыв обращения возвращает отзыв в рейтинг.

**Жалоба из звонка** (`fromCall: true`, только `Conduct`). Заявитель должен находиться в комнате идущего урока этого обмена (иначе 409 `not_in_call`). Обмен замораживается сразу, открытые уроки отменяются, комната закрывается, обоим приходит уведомление **`ExchangeFrozen`** (`exchangeId`). Отзыв обращения размораживает обмен (уведомление **`ExchangeUnfrozen`**), если нет другого активного обращения из звонка.

**Что видит второй участник.** Тот, на кого подано обращение, ничего не знает о нём, пока модератор не взял его в работу: ни уведомления, ни строки в `GET /me/cases?role=Against`, `GET /cases/{id}` до этого отвечает 404 `case_not_found`.

**Ограничения:** не больше 3 обращений в работе одновременно (409 `too_many_open_cases`), не больше 5 за сутки, считая отозванные (429 `case_rate_limited`), не больше одного активного обращения того же типа по тому же обмену (409 `case_already_open`).

**Ошибки 400:** `case_description_too_short` / `case_description_too_long`, `case_category_mismatch`, `case_review_required`, `case_review_not_allowed`, `case_from_call_conduct_only`, `too_many_evidence`, `evidence_not_in_exchange`. **409:** `filing_window_closed`, а также перечисленные выше.

**Обращение (`CaseDto`):** `id`, `type`, `category`, `status` (`Open`, `InProgress`, `WaitingResponse`, `Resolved`, `Appealed`, `AppealReview`, `Closed`, `Withdrawn`), `priority`, `exchangeId`, `reviewId`, `openedBy` и `against` (`id`, `name`, `avatarUrl`, `isDeleted`), `myRole` (`Opener` или `Against`), `description`, `fromCall`, `createdAt`, `evidenceMessageIds`, `decision`, `alsoWarned`, `decisionReason`, `resolvedAt` (решение показывается обеим сторонам, когда оно вынесено), `responseDeadlineAt` (пока ждём пояснений), `canWithdraw` (заявитель, статус `Open`).

**Для бэкенда.** Всё в одной транзакции под блокировкой обоих участников (`CaseService`); в журнал аудита пишутся `CaseOpened`, `CaseWithdrawn`, `ExchangeFrozen`, `ExchangeUnfrozen`. Доказательства сохраняются в `CaseEvidence` (внешний ключ на сообщение не даёт очистке чата их удалить). Закрытие комнаты звонка после коммита, при недоступном LiveKit комната закроется по расписанию. Форма запроса (тип, категория, `reviewId`, `fromCall`) проверяется до обращения к базе.

## Модератор (этап 6, шаг 3): очередь, взятие и чтение чата, контракт для фронтенда

Тег **Moderation**, префикс `/moderation`. Только роль `Moderator` (политика + проверка по базе: заблокированный модератор получает 403 `moderator_required`). Админ, суперадмин и обычный пользователь получают 403 на всех маршрутах. Служебные аккаунты по-прежнему не ходят в обычные маршруты обращений и чата.

| Метод | Что делает |
| --- | --- |
| `GET /moderation/cases?scope=&type=&priority=&page=&pageSize=` | `scope=Queue` (по умолчанию): не взятые, сначала высокий приоритет, затем ближайший срок; `scope=Mine`: взятые мной и не закрытые |
| `GET /moderation/cases/{id}` | Карточка: оба участника, описание, доказательства, оспариваемый отзыв (`review`), срок (`dueAt`, `isOverdue`), `hasChatAccess` |
| `POST /moderation/cases/{id}/take` | Взять в работу |
| `GET /moderation/cases/{id}/chat?before=&limit=` | История чата обмена (курсор и `limit` 1–100, как у обычного чата) |

**Конфликт интересов (раздел 3 ТЗ, правило 3).** Обращение нельзя взять (403 `conflict_of_interest`), если среди участников личный аккаунт модератора (`LinkedUserId`) или человек, с которым этот личный аккаунт когда-либо вёл обмен (в любом статусе обмена). Такие обращения не видны модератору ни в очереди, ни в карточке (404 `case_not_found`). Без привязанного личного аккаунта конфликта нет.

**Взятие.** Одна транзакция под блокировкой модератора и обоих участников: `Open → InProgress`, доступ к чату (`ChatAccessGrants`, причина `Case`) только на обмен этого обращения, записи аудита `CaseTaken` и `ChatAccessGranted`, участникам по **одному** уведомлению **`CaseTaken`** (`caseId`, `exchangeId`, `role`: `Opener` или `Against`; текст: модератор может прочитать переписку этого обмена). Повторное взятие и одновременное взятие вторым модератором: 409 `invalid_state_transition`, ничего не создаётся. Обвиняемый видит обращение (`GET /cases/{id}`) с этого момента.

**Чтение чата.** Нужен активный доступ, выданный именно по этому обращению (доступ привязан к обращению, а не только к обмену): иначе 403 `chat_access_denied` (не тот модератор, не брал, доступ закрыт решением, чужой обмен); обращения нет: 404 `case_not_found`. Каждый запрос (в том числе следующие страницы) пишет в `AuditLogs` строку `ChatRead` (кто, обращение в `details.caseId`, обмен в `targetId`, время, адрес) до выдачи данных; отказ чтением не считается. Участникам ничего не отправляется и непрочитанные не меняются. Удалённые сообщения приходят **с текстом** (`deletedAt` заполнен), у исправленных есть `revisions` (прежние тексты со временем правки), сообщения-доказательства помечены `isEvidence`. Только чтение: записи, отметки «прочитано» и обычного чата у модератора нет. Вложения показываются описанием (имя, тип, размер) без ссылки на скачивание.

**Для бэкенда.** `ModerationService.RevokeChatAccessAsync(caseId, actorId)` отзывает все действующие доступы по обращению (аудит `ChatAccessRevoked`, повтор безвреден); решение, закрытие апелляции и очистка в следующих шагах вызывают её в своей транзакции. После отзыва чтение сразу даёт 403. Проверка доступа: `ChatAccessPolicy` (роль `Moderator` + действующий доступ; администратору и суперадмину чат недоступен, даже если доступ на них записан).

## Пояснения по обращению (этап 6, шаг 4): контракт для фронтенда

Раздел 8.2 ТЗ. Модератор, взявший обращение, задаёт вопросы участникам; у них 48 часов на ответ.

| Метод | Кто | Что делает |
| --- | --- | --- |
| `POST /moderation/cases/{id}/questions` | модератор | Вопрос: `recipient` (`Opener`, `Against`, `Both`), `text` 1–2000 символов |
| `GET /moderation/cases/{id}/explanations` | модератор | Вся переписка по обращению |
| `GET /cases/{id}/explanations` | участник | Моя переписка: вопросы мне и мои ответы (видна, когда обращение взято) |
| `POST /cases/{id}/explanations` | участник | Ответ: `questionId`, `text` 1–2000 символов |

**Ход.** Вопрос переводит обращение `InProgress → WaitingResponse` и задаёт `responseDeadlineAt` (сейчас + 48 часов); получатели получают уведомление **`CaseExplanationRequested`** (`caseId`, `exchangeId`, `questionId`, `deadlineAt`). Вопрос обоим создаёт отдельный вопрос каждому, каждый видит только свой. Пока ждём ответов, второй запрос на пояснения невозможен (409 `explanation_already_requested`). Когда ответили на все вопросы, обращение возвращается в `InProgress` сразу; если кто-то промолчал, то через 48 часов его возвращает задача `CaseJobs.ExplanationDueAsync` (с тем, что есть). После возврата можно спрашивать снова; всего по обращению не больше 10 вопросов (409 `too_many_questions`). Доступ модератора к чату на время ожидания сохраняется.

**Уведомления.** Участнику, не ответившему к моменту «срок минус 6 часов»: **`CaseExplanationReminder`** (`caseId`, `exchangeId`, `questionId`, `deadlineAt`; одно на круг пояснений, тем, кто уже ответил, не приходит). Модератору, когда обращение вернулось в работу: **`CaseReturnedToWork`** (`caseId`, `reason`: `AllAnswered` или `Expired`). Оба только в интерфейсе, письма не уходят. Напоминание ставится вместе с задачей срока (`CaseJobs.ExplanationReminderAsync`), отметка `Cases.ExplanationReminderSentAt` не даёт разослать дважды и сбрасывается новым кругом вопросов.

**Ошибки ответа.** 404 `case_not_found` (чужое обращение, обвиняемому не взятое), 404 `question_not_found` (чужой или несуществующий вопрос), 409 `explanation_not_requested` (обращение не в `WaitingResponse`), 409 `explanation_window_closed` (срок вышел), 409 `question_already_answered`, 400 `case_message_empty` и `case_message_too_long`. Вопрос: 404, если обращение чужое или не взято, 409 `invalid_state_transition`, если обращение не в работе.

**В карточках.** `CaseDto.pendingQuestions` — сколько вопросов ждут моего ответа (когда `status: WaitingResponse`), `responseDeadlineAt` — срок; `ModerationCaseDto.pendingQuestions` — сколько заданных модератором вопросов без ответа. Элемент переписки (`ExplanationDto`): `id`, `kind` (`Question` или `Answer`), `senderId`, `recipientId`, `text`, `inReplyToId`, `answered` (у вопроса), `createdAt`.

**Для бэкенда.** Всё в транзакции под блокировкой модератора и обоих участников. Задача возврата ставится при вопросе на момент срока, сверяется с состоянием (повтор и ранний запуск безвредны, допуск 5 секунд). Аудит: `ExplanationRequested`, `ExplanationAnswered`, `ExplanationExpired` (без исполнителя).

## Решения по обращениям (этап 6, шаг 5): контракт для фронтенда

Раздел 8.3 ТЗ. Тег **Moderation**, только модератор, взявший обращение.

| Метод | Что делает |
| --- | --- |
| `POST /moderation/cases/{id}/resolve` | Решение: `decision`, `alsoWarned`, `reason` (20–2000 символов, его видят обе стороны) |
| `POST /moderation/cases/{id}/freeze-exchange` | Заморозить обмен, пока жалоба рассматривается |
| `POST /moderation/cases/{id}/unfreeze-exchange` | Разморозить обмен |

**Решения и их последствия.**

| Тип | `decision` | Что происходит |
| --- | --- | --- |
| Спор по отзыву | `ReviewKept` | Отзыв возвращается в рейтинг (`Frozen → Published`) |
| Спор по отзыву | `ReviewHidden` | Отзыв скрыт (`Hidden`), пропадает из списка в профиле, рейтинг получателя пересчитан |
| Жалобы | `Warning` | Предупреждение тому, на кого подана жалоба (действует 12 месяцев); обмен, замороженный жалобой, размораживается |
| Жалобы | `ExchangeEnded` | Обмен завершается принудительно (`Aborted`, причина `moderation`), открытые уроки отменяются, обоим `ExchangeAborted`, у обоих право на отзыв по обычным правилам 8.1 |
| Жалобы | `FrozenBanRequested` | Обмен замораживается (если идёт), создаётся запрос на блокировку для админа (`BanRequests`, `Pending`); аккаунт блокирует только админ |
| Жалобы | `NoViolation` | Нарушений нет; обмен, замороженный жалобой, размораживается |

`alsoWarned: true` добавляет предупреждение виновному (к `ReviewHidden`, `ExchangeEnded`, `FrozenBanRequested`; у спора виновный — автор отзыва). Третье действующее предупреждение за 12 месяцев само создаёт запрос на бан (один необработанный на пользователя; отозванные и истёкшие не считаются). Обмен, замороженный жалобой, размораживается решением `Warning` или `NoViolation`, только если по нему нет другой незакрытой жалобы.

**Общее для решения.** Одна транзакция под блокировкой модератора и обоих участников: решение, последствия, **отзыв доступа к чату** (аудит `ChatAccessRevoked`, после чего чтение 403), аудит `CaseResolved`, уведомления. Обе стороны получают **`CaseResolved`** (`caseId`, `exchangeId`, `role`, `decision`, `alsoWarned`, `youWereWarned`, `appealUntil` = решение + 7 дней) и **письмо** на языке интерфейса (tg, ru, en) с решением, сроком апелляции и ссылкой на обращение (`Frontend:CasePath`, по умолчанию `/cases/{id}`); виновный при предупреждении получает ещё `WarningIssued` (только в интерфейсе). Решение видно обеим сторонам в `GET /cases/{id}` (`decision`, `alsoWarned`, `decisionReason`, `resolvedAt`). Через 7 дней без апелляции обращение закрывается задачей `CaseJobs.CloseIfDueAsync` (`Closed`, аудит `CaseClosed`).

**Ошибки решения.** 400: `case_decision_not_allowed` (решение не подходит типу), `case_warning_not_combinable`, `case_reason_too_short`/`case_reason_too_long`. 404: чужое обращение. 409: `waiting_for_explanations` (ждём пояснений), `exchange_not_open` (`ExchangeEnded`, а обмен уже закрыт), `invalid_state_transition` (уже решено). Заморозка и разморозка: 409 `exchange_not_active`, `exchange_not_frozen`, `case_type_not_supported` (спор по отзыву обмен не замораживает), `case_not_in_work`.

**Для бэкенда.** Пока по пользователю есть обращение, не отозванное и не закрытое, `IErasureHold` (`OpenCaseErasureHold`) удерживает его личные данные при удалении аккаунта; обращение в статусе `Resolved` удерживает, пока идёт срок апелляции.

## Апелляция, админ и блокировка (этап 6, шаг 6): контракт для фронтенда

Разделы 3, 8.2 и 8.3 ТЗ. Тег **Admin** (`/admin`, роли Admin и SuperAdmin; модератор и пользователь получают 403) и дополнения к **Cases** и **Moderation**.

**Досрочное завершение ожидания.** `POST /moderation/cases/{id}/stop-waiting` (`reason` 20–2000 символов): `WaitingResponse → InProgress`, причина пишется в аудит (`ExplanationStopped`), поздние ответы участников не принимаются (409 `explanation_not_requested`), после этого можно выносить решение. Заморозка и разморозка обмена (`freeze-exchange`, `unfreeze-exchange`) работают и в `WaitingResponse`, ожидание при этом не сбивается.

**Апелляция участника.** `POST /cases/{id}/appeal` (`reason` 20–2000 символов): любая из сторон, один раз, пока не прошло 7 дней после решения (`CaseDto.canAppeal`). Обращение переходит в `Appealed`, второй стороне и всем админам приходит **`CaseAppealed`**. В `CaseDto` появляется `appeal` (причина, `reviewInProgress`, `outcome`, `finalDecision`, `decidedAt`; заключение проверяющего участнику не показывается). Ошибки: 400 `appeal_reason_too_short`, `appeal_window_closed`; 409 `invalid_state_transition` (решения ещё нет или апелляция уже подана); 404 `case_not_found`.

| Метод | Что делает |
| --- | --- |
| `GET /admin/cases?status=&type=&page=&pageSize=` | Все обращения, новые сверху (апелляции: `status=Appealed`, идущие проверки: `AppealReview`) |
| `GET /admin/cases/{id}` | Материалы: описание, оспариваемый отзыв, **приложенные сообщения** (удалённые с текстом, с прежними версиями), пояснения, предупреждения, запросы на бан, апелляция |
| `GET /admin/moderators` | Действующие модераторы (выбор проверяющего) |
| `POST /admin/cases/{id}/appeal/reviewer` | Назначить проверяющего: `moderatorId` |
| `POST /admin/cases/{id}/appeal/decision` | Окончательное решение |
| `POST /moderation/cases/{id}/appeal-conclusion` | Заключение проверяющего: `conclusion` 20–4000 символов |
| `GET /moderation/cases?scope=Appeals` | Мои повторные проверки |
| `GET /admin/ban-requests?status=` | Запросы на блокировку |
| `POST /admin/ban-requests/{id}/approve` и `/reject` | Решение по запросу (`comment` необязателен) |

**Админ не читает чат.** Материалы содержат только сообщения, приложенные к обращению как доказательства; каждый просмотр пишется в аудит (`CaseMaterialsRead`), маршруты чата админу по-прежнему закрыты (403).

**Повторная проверка.** Админ назначает **другого** модератора: не автора решения (400 `appeal_reviewer_must_differ`), не участника, без конфликта интересов (409 `conflict_of_interest`: личный аккаунт или бывший партнёр), действующего модератора (409 `not_a_moderator`). Обращение `Appealed → AppealReview`, проверяющему выдаётся доступ к чату этого обмена (причина `AppealReview`, аудит `AppealReviewAssigned` и `ChatAccessGranted`), **обе стороны получают по одному `CaseAppealReviewAssigned`** (`role`), проверяющий — `AppealReviewAssigned`. Проверяющий видит карточку с причиной апелляции (`isAppealReviewer`, `appeal`), читает чат тем же маршрутом (`GET /moderation/cases/{id}/chat`, каждый запрос в аудит) и пишет заключение: **его доступ к чату закрывается** (аудит `ChatAccessRevoked`, дальше 403), обращение возвращается в `Appealed`, назначившему админу приходит `AppealConclusionReady`. Пока идёт проверка, решение выносить нельзя (409 `appeal_review_in_progress`).

**Окончательное решение** (`outcome`): `Upheld` решение остаётся (`reason` необязателен); `Overturned` отменяет его (нужен `reason`; для спора итогом становится `ReviewKept`, для жалоб `NoViolation`; если решение и так нейтральное, 409 `nothing_to_overturn`); `Changed` заменяет на `newDecision` (+ `alsoWarned`, нужен `reason`). `newDecision` указывается только при `Changed` (400 `appeal_final_decision_mismatch`). Обращение `Closed`, доступ к чату закрыт, **обе стороны получают `AppealDecided` и письмо** (tg, ru, en).

| Что откатывается при отмене или замене | Как |
| --- | --- |
| Скрытый отзыв (`ReviewHidden`) | Возвращается (`Published`), рейтинг пересчитан, аудит `ReviewRestored` |
| Предупреждение (`Warning` или `alsoWarned`) | Отзывается (`WarningRevoked`, уведомление **`WarningRevoked`** виновному); автоматический запрос на бан по трём предупреждениям отклоняется, если действующих стало меньше трёх |
| Заморозка с запросом на бан (`FrozenBanRequested`) | Необработанный запрос отклоняется, обмен размораживается, если по нему нет других жалоб |
| Принудительное завершение обмена, одобренная блокировка | **Не откатываются** |

Новое решение (`Changed`) применяется так же, как решение модератора (последствия, предупреждение админом, запрос на бан, принудительное завершение).

**Запросы на блокировку.** Одобрение (`approve`): аккаунт **сразу** `Blocked`, **все сеансы отозваны** (refresh-токены), **соединения SignalR обрываются** (хабы `/hubs/chat` и `/hubs/notifications`; новое подключение заблокированного отклоняется, пока токен ещё жив), активные и замороженные обмены прерываются (`Aborted`, причина `account_blocked`, партнёру **нейтральная** причина `partner_unavailable` и уведомление `ExchangeAborted`), открытые уроки отменяются, приглашения на отзыв ставятся по обычным правилам. Ожидающие заявки на обмен заблокированного в обе стороны становятся `Expired` без уведомлений отправителям (как при блокировке пользователем). Заблокированному уходит **письмо** «аккаунт заблокирован за нарушение правил платформы» на его языке (tg, ru, en), без деталей обращения. Токен доступа живёт до истечения, но сервисы заблокированного не пускают (403 `account_not_active`). Запросившему модератору приходит **`BanRequestDecided`** (`approved`). Отклонение (`reject`): обмен, замороженный по этому обращению, размораживается, если по нему нет других незакрытых жалоб. 404 `ban_request_not_found`; 409 `invalid_state_transition` (уже решён); 400 `user_not_active` (аккаунт уже не действующий, запрос остаётся необработанным).

**Для бэкенда.** Последствия решений и их откат: `CaseConsequences` (общий код модератора и админа). Всё в одной транзакции под блокировкой участников. Реестр соединений SignalR (`ConnectionRegistry`) хранится в памяти процесса. Выбор проверяющего защищён от конфликта интересов теми же правилами, что и взятие обращения.

## Хранение и очистка (этап 6, шаг 7): для бэкенда и фронтенда

Раздел 10.3 ТЗ. Две повторяющиеся задачи Hangfire (`RecurringCatalog`, регистрируются при запуске сервера задач, UTC): **сверка обращений** каждый час (`CaseMaintenanceJobs.SweepAsync`) и **очистка чатов** каждую ночь в 03:30 (`ChatCleanupJobs.PurgeAsync`). Обе идемпотентны, сбой одного обращения или обмена не мешает остальным. Дополнение интерфейса `IBackgroundJobs.Recurring`: повторная регистрация с тем же идентификатором обновляет расписание.

**Очистка чатов.** Через 90 дней после закрытия обмена удаляются его сообщения, прежние версии и вложения (строки в `Attachments` и файлы в хранилище), **если по обмену нет открытого обращения или апелляции** (любой статус, кроме `Withdrawn` и `Closed`, в том числе `Resolved` на время срока апелляции). **Сообщения, приложенные к обращению, не удаляются** (внешний ключ `CaseEvidence → Messages` не даёт этого сделать и в базе), они хранятся год после закрытия обращения; у отозванного обращения защита снимается сразу. Когда защита снята, следующая очистка удаляет и их. `Exchanges.ChatPurgedAt` заполняется, когда в обмене не осталось сообщений (до тех пор обмен остаётся кандидатом); в аудит пишутся `ChatPurged` и `EvidenceReleased` (без исполнителя). Участникам чат недоступен уже через 30 дней после закрытия (`Archived`), очистка меняет только хранение.

**Сверка обращений** (догоняет то, что разовые задачи не успели, если Hangfire был выключен): возвращает из `WaitingResponse`, когда срок ответа вышел; закрывает `Resolved` старше 7 дней без апелляции; **отзывает «зависшие» доступы к чату** (доступ по обращению нужен только в `InProgress` и `WaitingResponse`, доступ проверяющего по апелляции только в `AppealReview`; аудит `ChatAccessRevoked` без исполнителя); **отмечает просроченные обращения** (не взято, а срок реакции 24 или 72 часа вышел): один раз, `Cases.OverdueMarkedAt`, аудит `CaseOverdue`, всем админам уведомление **`CaseOverdue`** (`caseId`, `priority`, `dueAt`); снимает удержание данных удалившихся пользователей.

**Админ.** В `AdminCaseDto` добавлены `dueAt` и `isOverdue` (срок реакции прошёл, а обращение не взято).

**Удержание данных при удалении аккаунта** (`IErasureHold`) снимается, когда обращение закрыто (7 дней без апелляции или окончательное решение админа) или отозвано: личные данные удалившейся стороны стираются (`AccountDeletionService.CompleteErasureAsync`, если других открытых обращений по ней нет); тот же проход делает часовая сверка.

## Контракт для фронтенда и проверка модерации (этап 6, шаг 8)

**Сводка для фронтенда:** `docs/FRONTEND-CASES.md` (роли и экраны, подача, статусы, решения, каталог уведомлений с данными и признаком письма, коды ошибок, сроки, проверка локально). Тест `FrontendDocsTests` не даёт ей отстать от кода: каждый тип уведомления этапа 6 и каждое значение `CaseStatus`, `CaseType`, `CaseCategory`, `CaseDecision`, `AppealOutcome` обязаны быть в ней упомянуты.

**Сроки обращений (секция `Cases`).** `HighSlaMinutes` (1440), `NormalSlaMinutes` (4320), `ExplanationWindowMinutes` (2880), `ExplanationReminderBeforeMinutes` (360), `AppealWindowMinutes` (10080). Значения по умолчанию равны ТЗ; другие допустимы только в Development и Testing, в боевой среде приложение с ними не запустится (как `Lessons`). В `appsettings.Development.json` сроки сокращены до минут (3, 5, 4, 2, 6): вопрос, напоминание, возврат в работу, апелляция и закрытие проверяются руками за несколько минут. Сроки подачи (14, 30, 90 дней), слепая публикация и очистка чатов не меняются. Хранятся в `CaseTiming` (общие на процесс; тестовая фабрика задаёт значения ТЗ и в Development, поэтому сокращённые настройки не «залипают» между тестами).

**Сидеры (только Development, скрыты в OpenAPI, вне Development 404).**
- `POST /dev/seed-staff`: один раз создаёт модератора `dev-moderator@example.com`, второго модератора `dev-moderator-2@example.com` и админа `dev-admin@example.com`; в ответе `accessToken` каждого (служебным вход требует 2FA, для разработки выдаются токены; пароль виден только при создании).
- `POST /dev/seed-case` `{ type, stage, decision }` (все поля необязательны): двое новых пользователей (вход по почте и паролю, токены тоже есть), обмен с перепиской (последние два сообщения приложены как доказательства) и обращение, доведённое до стадии (`Open`, `Taken`, `Waiting`, `Resolved`, `Appealed`, `AppealReview`) **настоящими сервисами**: подача, взятие, вопросы, решение, апелляция, назначение проверяющего. Поэтому появляются настоящие уведомления, письма и фоновые задачи с сокращёнными сроками. Тип `ReviewDispute` создаёт закрытый обмен с засчитанным уроком и двумя опубликованными отзывами (урок и закрытие записываются напрямую, отзывы через сервис).

## Сидеры админских сценариев и итог этапа 7 (шаг 10): для бэкенда

**Сидеры (только Development, в схему и Scalar не попадают).** `POST /dev/seed-staff` теперь создаёт и **суперадмина** `dev-superadmin@example.com` (пароль `Dev-staff-pass-1`, 2FA с известным ключом, как у остальных); форма ответа прежняя, добавлено поле `superAdmin` (фронтенд собирает токены по всем объектам с `accessToken`, поэтому новый аккаунт подхватывается без правок). Новый `POST /dev/seed-admin` (`AdminSeedDevTools`): весь служебный состав плюс данные для каждого админского экрана (личные пользователи, предложения навыков, запрос на бан, приглашение с готовой ссылкой, запрос на привязку с готовой ссылкой); описание ответа в `FRONTEND-ADMIN.md`, раздел 10. Тесты: `AdminSeedDevToolsTests` (суперадмин и его права, данные и работающие ссылки, повторный вызов, отсутствие вне Development).

**Проверка на настоящем стеке** (Development, PostgreSQL, Garage, Mailpit, Hangfire; приложение запущено с `dotnet run`, обращения по HTTP): сидеры; чтение админских разделов (пользователи, модераторы, приглашения, предложения навыков, запросы на бан, журнал админа и суперадмина, статистика админа и модератора, настройки); админ не открывает суперадминский маршрут (403); блокировка и разблокировка пользователя; панель Hangfire суперадмином: билет → сессия → страница 200, без cookie 401; ссылки приглашения и привязки из сидера открываются. В Hangfire зарегистрированы четыре повторяющиеся задачи (`cases-sweep`, `chat-cleanup`, `notification-cleanup`, `bucket-reconciliation`). Сверка бакета и очистка уведомлений проверяются интеграционными тестами на том же Garage и PostgreSQL (Testcontainers).

**Документы для запуска.** Что нужно сделать на боевом сервере: `docs/RELEASE-CHECKLIST.md` (по порядку, у каждого пункта где описано и как проверить). Чем реализация отличается от ТЗ v2.2: `docs/TZ-CHANGES.md`.

## Уборка, права БД и отзыв запросов (этап 7, шаг 9): для бэкенда

### Ночные задачи

Расписание в `RecurringCatalog` (UTC, регистрируется при запуске сервера Hangfire): сверка обращений каждый час, очистка чатов 03:30, **очистка уведомлений 04:15**, **сверка бакета 04:45**. Настройки секции `Maintenance` (`MaintenanceOptions`, проверяются при запуске: приложение с недопустимыми значениями не стартует):

| Ключ | По умолчанию | Что делает |
| --- | --- | --- |
| `Maintenance:Notifications:ReadRetentionDays` | 90 | Сколько дней хранятся прочитанные уведомления |
| `Maintenance:Notifications:UnreadRetentionDays` | 180 | Сколько дней хранятся непрочитанные |
| `Maintenance:Notifications:BatchSize` | 1000 | Сколько уведомлений удаляется одним запросом |
| `Maintenance:Notifications:MaxBatchesPerRun` | 100 | Сколько пачек каждого вида за ночь; остальное доберёт следующая |
| `Maintenance:BucketReconciliation:DeleteOrphans` | **false** | Удалять ли файлы-сироты; **false: только отчёт** |
| `Maintenance:BucketReconciliation:MinAgeHours` | 24 | Файлы моложе не трогаются (файл кладётся раньше, чем появляется строка в базе) |
| `Maintenance:BucketReconciliation:MaxOrphansPerRun` | 1000 | Предел сирот за запуск (в отчёте и при удалении) |
| `Maintenance:BucketReconciliation:CheckBatchSize` | 500 | Сколько ключей сверяется с базой одним запросом |

Переменные окружения: `Maintenance__BucketReconciliation__DeleteOrphans=true` и так далее.

**Очистка уведомлений** (`NotificationCleanupJobs`). Возраст считается от времени создания (у обновлённого счётчика непрочитанных сообщений от последнего обновления). Удаляется пачками `DELETE ... WHERE Id IN (старейшие N)` (индекс `IX_Notifications_IsRead_CreatedAt`), чтобы таблица не блокировалась надолго; за запуск не больше `MaxBatchesPerRun` пачек каждого вида. Если что-то удалено, в аудит пишется `NotificationsPurged` (сколько прочитанных и непрочитанных, какие сроки), если нет, запись не пишется. Сбой одного вида не ломает запуск: уже удалённое остаётся удалённым.

**Сверка бакета** (`BucketReconciliationJobs`). Перечисляются объекты с префиксом `exchanges/` (это вложения чатов; `IFileStorage.ListAsync`), старше `MinAgeHours` проверяются пачками по базе (`Attachments.StorageKey`); у кого строки нет, тот сирота. **Первый запуск на боевой среде только отчёт** (по умолчанию так и есть): сироты не удаляются, в журнал аудита пишется `BucketReconciled` (`mode: report`, сколько просмотрено, сколько моложе суток, сколько сирот и их размер, образец до 50 ключей) и в лог каждый ключ. Порядок на боевой среде: дождаться первого отчёта (`/superadmin/audit`, действие `BucketReconciled`), сверить с ожиданиями (сироты должны быть оборванными загрузками и хвостами очистки чатов), затем включить `DeleteOrphans=true` и перезапустить; отчёт станет `mode: delete` со счётчиками `deleted` и `failed`. Если хранилище или база недоступны, сверка прерывается без отчёта и без удаления. Аватары и выгрузки данных не сверяются.

### Права роли приложения на боевой базе

Журнал аудита защищён двумя слоями: триггер запрещает UPDATE, DELETE и TRUNCATE для **любой** роли (в том числе владельца) в любой среде, а на боевой базе приложение работает под отдельной ролью `skillswap_app`, у которой на `AuditLogs` вообще нет этих прав. Скрипт: `backend/scripts/create-app-role.sql` (проверяется тестом `AppRolePermissionsTests` на настоящем PostgreSQL).

Порядок при деплое (боевая база создана, владелец схемы `skillswap_owner`, он же применяет миграции):

```
# 1. Один раз: создать роль приложения (пароль из хранилища секретов, в репозитории его нет).
psql -d skillswap -U skillswap_owner -c "CREATE ROLE skillswap_app LOGIN PASSWORD '<пароль>';"

# 2. Каждый деплой: применить миграции строкой ВЛАДЕЛЬЦА (не приложения).
ConnectionStrings__Default="Host=...;Database=skillswap;Username=skillswap_owner;Password=..." \
  dotnet ef database update --project src/SkillSwap.Infrastructure --startup-project src/SkillSwap.Api

# 3. Каждый деплой после миграций: выдать права (скрипт можно запускать повторно).
psql -v ON_ERROR_STOP=1 -d skillswap -U skillswap_owner -f backend/scripts/create-app-role.sql

# 4. Приложение запускается со строкой роли skillswap_app.
ConnectionStrings__Default="Host=...;Database=skillswap;Username=skillswap_app;Password=..."
```

Что получает роль: на все таблицы `public` SELECT, INSERT, UPDATE, DELETE (и на будущие таблицы по умолчанию); на `AuditLogs` только SELECT и INSERT (повторный запуск скрипта отбирает лишнее, выданное вручную); на `__EFMigrationsHistory` только SELECT (в боевой среде приложение схему не меняет); схему `hangfire` (очередь фоновых задач) роль создаёт и меняет сама. Создавать таблицы в `public` роль не может. **Проверка после первого деплоя** (под ролью приложения): `UPDATE "AuditLogs" SET "Action" = "Action" WHERE false;` должно ответить `permission denied for table AuditLogs`, а `INSERT` в журнал приложение делает при каждом действии. Если миграции применяет роль, отличная от запускающей скрипт, `ALTER DEFAULT PRIVILEGES` не сработает для её новых таблиц: запускать скрипт после каждого деплоя (п. 3) обязательно.

### Отзыв запросов заблокированного и снятого

`PendingStaffRequestsRevoker` вызывается внутри транзакции из `AccountBlocker.ApplyAsync` (ручная блокировка и одобренный бан) и из `ModeratorAdminService.RemoveCoreAsync` (снятие модератора и админа). Отзывает всё, что ещё действует (не принято, не отклонено, не отменено, срок не вышел): **приглашения сотрудников**, отправленные этим аккаунтом (`StaffInvitation.Revoke`: ссылка из письма перестаёт работать, служебный аккаунт по ней не создаётся) и **запросы на привязку личного аккаунта**, где аккаунт служебный (`StaffId`) или личный (`PersonalUserId`; `PersonalLinkRequest.Cancel`). **Письма получателям не уходят** и уведомлений нет. Каждый отзыв пишется в аудит (`StaffInvitationRevoked` или `PersonalLinkRequestCancelled`, исполнитель тот, кто заблокировал или снял, в деталях `reason`: `account_blocked` или `staff_removed`), а в записях `UserBlocked` и `StaffRemoved` есть счётчики `invitationsRevoked` и `linkRequestsCancelled`. Истёкшие, принятые и отозванные ранее записи не меняются, чужие запросы тоже.

### Тесты

`HousekeepingTests` (очистка: сроки, настройки, пачки и предел запуска, аудит; сверка: отчёт по умолчанию, удаление с флагом, возраст файла, предел за запуск, чужие префиксы, недоступное хранилище, расписание), `AppRolePermissionsTests` (права роли на настоящем PostgreSQL), `PendingRequestsRevocationTests` (блокировка, снятие модератора и админа, ссылки перестают работать, писем нет).

## Статистика (этап 7, шаг 8): для бэкенда

Контракт для фронтенда: `docs/FRONTEND-ADMIN.md`, раздел 9 (тег **Stats**, маршруты `GET /admin/stats` и `GET /moderation/stats`).

- **Состав.** `Stats/StatsService` и `IStatsStore` (`Infrastructure/Stats/StatsStore.cs`). Только агрегаты: запросы `COUNT` и `GROUP BY`, наружу не попадают имена, почты, адреса и идентификаторы людей, обращений и обменов (тест `The_answers_are_aggregates_only_and_dictionaries_list_every_value` перебирает имена полей ответа и ищет значения). Словари по типам, решениям и статусам заполняются всеми значениями перечисления (`Fill<TEnum>`), форма ответа не зависит от данных.
- **Время реакции.** Первое взятие обращения хранится в колонках `Cases.FirstTakenAt` и `Cases.FirstTakenById`: их ставит `Case.Take` при первом взятии, а при возврате в очередь (`ModeratorCaseReleaser`) и повторных взятиях они не меняются. Старые обращения заполнила миграция `AddCaseFirstTakenAndStatsIndexes` из журнала аудита (самая ранняя запись `CaseTaken` по обращению; SQL лежит в константе `BackfillSql`, её проверяет тест). Реакция = `FirstTakenAt` минус `Cases.CreatedAt`, уложился ли в срок = `FirstTakenAt` не позже `Cases.DueAt`. У админа когорта: обращения, **поданные** за период. У модератора: обращения, у которых **он** взял первым (`FirstTakenById`) и сделал это в периоде. Среднее и медиана считаются в памяти по списку наблюдений (`StatsService.Summarize`, отрицательное время из-за расхождения часов считается нулём). Число взятий у модератора (`taken`) по-прежнему считается по журналу аудита, потому что считает каждое взятие, в том числе повторное.
- **Просрочки.** `overdueNow`: `Status = Open` и `DueAt` раньше сейчас; `overdueMarkedInPeriod`: поле `OverdueMarkedAt`, которое ставит ежечасная задача (`Maintenance`) и которое объявляется админам.
- **Периоды.** `from` включительно, `to` не включая; границы приводятся к UTC (время без пояса считается UTC), по умолчанию 30 дней до текущего момента, предел 366 дней (`invalid_period`, `period_too_long`). Кэша нет: цифры всегда свежие.
- **Индексы под статистику.** Та же миграция добавила индексы по датам: `Users` (`CreatedAt`, `LastActiveAt` без пустых), `Exchanges` (`CreatedAt`, `ClosedAt+Status` без пустых), `Sessions` (`StartAt`), `Cases` (`CreatedAt`, `ResolvedAt+Decision` и `ClosedAt` без пустых, `OverdueMarkedAt` без пустых, `FirstTakenById+FirstTakenAt` без пустых). Прежние частичные индексы (очистка чатов, закрытие решённых обращений) остались: EF склеивает индексы по одним и тем же колонкам, поэтому новые сделаны составными.
- **Тесты.** `StatsApiTests` (8: права, период и его проверки, форма и отсутствие персональных данных, счётчики платформы, решённые обращения, точные значения реакции и доли в срок, просрочки, личная статистика модератора до цифры), `StatsSummaryTests` (5, сводка наблюдений). Точные значения проверяются узким окном вокруг контролируемой даты подачи, общие счётчики приращениями (база тестов общая).

## Журнал аудита и панель Hangfire (этап 7, шаг 7): для бэкенда

Контракт для фронтенда: `docs/FRONTEND-ADMIN.md`, раздел 8 (теги **Audit** и **Superadmin hangfire**).

- **Журнал.** `Audit/AuditJournalService` и `IAuditJournalStore` (`Infrastructure/Audit/AuditJournalStore.cs`), контроллер `AuditController`: четыре маршрута (`/admin/audit`, `/admin/audit/actions`, `/superadmin/audit`, `/superadmin/audit/actions`). Видимость по **текущей роли** в базе: суперадмин видит всё (включая записи без исполнителя, то есть действия системы, и адреса), админ записи, где исполнитель сейчас модератор или он сам, без адресов (`IpAddress` обнуляется в сервисе, а не на экране); на админском маршруте суперадмин тоже видит всё. Область применяется ДО пользовательских фильтров, поэтому фильтр `actorId` не расширяет видимость. Индексы таблицы (`ActorId, CreatedAt`, `TargetType, TargetId, CreatedAt`, `Action, CreatedAt`) уже есть; выборка идёт по `CreatedAt` убывающе, подсчёт `COUNT` по тому же фильтру. Подробности (`jsonb`) отдаются как есть; запись с нечитаемым JSON отдаётся без них. Базу тут не меняем: записи только добавляются (право `UPDATE` и `DELETE` на `AuditLogs` у боевого пользователя закрывается в шаге 9).
- **Панель.** `Staff/HangfirePanelService` (выдаёт билет после `StepUpService`, пишет аудит `HangfirePanelOpened`, 409 `dashboard_unavailable`, если Hangfire выключен) и `Infrastructure/Jobs/HangfireDashboardAccess` (билеты и сессии в памяти процесса, хранятся хэши SHA-256; билет живёт минуту и сгорает при первой попытке, сессия 30 минут). `HangfireDashboardFilter` (`IDashboardAsyncAuthorizationFilter`) обменивает `?ticket=` на сессию и ставит cookie `skillswap_hangfire` (`HttpOnly`, `SameSite=Strict`, путь `/hangfire`, `Secure` при https), а на каждый запрос панели проверяет по базе, что владелец сессии действующий суперадмин (снятого не пускает сразу). Стандартный фильтр «только локальные адреса» отключён (`Authorization = []`), решает асинхронный. Панель подключается при `Hangfire:Enabled=true` во всех средах (`UseSuperAdminHangfireDashboard` заменил `UseDevelopmentHangfireDashboard`); при `false` маршрута нет (404).
- **Ограничение.** Сессии панели хранятся в памяти: при нескольких экземплярах сервера (Redis) билет нужно использовать на том же экземпляре, который его выдал; при переходе на несколько экземпляров хранилище выносится в Redis (запись в DEFERRED).
- **Тесты.** `AuditJournalApiTests` (9: что видит суперадмин и админ, адреса, реальное действие админа, права, фильтры и страницы, ошибки, список действий); `HangfireRuntimeTests` (панель закрыта без сессии во всех средах, вход по билету с настоящим сервером Hangfire, повтор билета, снятие суперадмина, права и подтверждение, выключенный Hangfire, срок билета и сессии).

## Системные настройки суперадмина (этап 7, шаг 6): для бэкенда

Контракт для фронтенда: `docs/FRONTEND-ADMIN.md`, раздел 7 (тег **Superadmin settings**, маршрут `/superadmin/settings`, политика `Policies.SuperAdmin`).

- **Реестр.** `Settings/SystemSettingDefinitions.cs`: единственный источник ключей, типов (`Bool`, `Int`), границ и значений по умолчанию (`SystemSettingDefinition.Normalize` проверяет и приводит значение: флаг `true`/`false`, число из ASCII-цифр до 9 знаков в границах). Менять через API можно только настройки из реестра; новый ключ в `SystemSettingKeys` обязан попасть в реестр (тест `SystemSettingDefinitionsTests` сверяет их). Значения по умолчанию в коде (`ExchangeRequestService`) берутся из реестра, чтобы не расходились с тем, что видит суперадмин.
- **Изменение.** `SystemSettingsAdminService.UpdateAsync`: сначала форма (`no_settings`, `unknown_setting`, `duplicate_setting`, `invalid_setting_value`, `reason_too_long`), потом `StepUpService` (пароль и код на каждый запрос; отказ по значению код не сжигает), затем одна транзакция: каждая настройка, значение которой реально меняется, пишется (`ChangeValue`, нет строки — создаётся с описанием из реестра) и попадает в аудит `SettingChanged` (цель `SystemSetting` без id, в деталях `key`, `oldValue` (при отсутствии строки значение по умолчанию), `newValue`, `reason`). Те же значения не пишутся и в аудит не попадают.
- **Действие сразу.** Кэша нет: `ISystemSettingsStore` читает значение из базы при каждом использовании (подбор, заявки, аватары), поэтому новое значение действует со следующего запроса. `SystemSettingsStore` реализует и чтение, и `ISystemSettingsAdminStore`. Миграции не нужны (таблица и значения по умолчанию уже были).
- **Тесты.** `SystemSettingsApiTests` (18): права, список, изменение с аудитом старого и нового значения, действие сразу, то же значение, отсутствующая строка, проверки значений, откат пакета, подтверждение на каждый запрос; после каждого теста значения по умолчанию возвращаются (настройки общие для всей базы тестов).

## Авторизация служебных маршрутов (этап 7): одна политика на всех

- **Политики.** `Api/Auth/StaffAuthorization.cs`: `Policies.Moderator`, `Admin` (админ и суперадмин), `Staff` и `SuperAdmin`. Каждая = проверка роли в токене (быстрый отказ без обращения к базе) плюс `ActiveStaffRequirement`, которую проверяет `ActiveStaffHandler`: аккаунт есть в базе, служебный (`AccountType = Staff`), `Status = Active` и **роль в базе** входит в разрешённые (разжалованный по старому токену отказан). Обращение к базе одно на запрос (`IProfileStore.FindUserAsync`). При отказе обработчик бросает `ForbiddenException("staff_not_active")`, общий обработчик ошибок отдаёт 403 с этим кодом; токен чужой роли обработчик не трогает (его отклоняет проверка роли, 403 без тела).
- **Правило.** Любой маршрут под `/admin`, `/superadmin` и `/moderation` (читающий или изменяющий) обязан использовать только эти политики; голая роль (`[Authorize(Roles = ...)]`) нигде не допускается. Это проверяет тест `ActiveStaffPolicyTests.Every_admin_superadmin_and_moderation_route_uses_a_staff_policy_and_never_a_bare_role` по метаданным всех маршрутов: новый маршрут с голой ролью или без политики уронит его. Проверки `RequireAdminAsync` и `RequireModeratorAsync` в сервисах остаются как вторая линия защиты.
- **Что не покрыто политикой.** Чат обмена для участников и SignalR-хабы: там своя проверка статуса (`account_not_active` в сервисах, `ConnectionRegistry`). Тесты: `ActiveStaffPolicyTests` (снятый модератор с живым токеном и действующим доступом к чату: 403 на взятие, чтение чата, очередь, карточку, решение; снятый и удалённый админ: 403 на всех админских чтениях, в том числе каталог и материалы обращения; смена роли в базе; другие роли).

## Предложения навыков и каталог (этап 7, шаг 5): для бэкенда

Контракт для фронтенда: `docs/FRONTEND-ADMIN.md`, раздел 6 (тег **Admin skill suggestions**).

- **Рассмотрение.** `Skills/SkillSuggestionAdminService` и `ISkillSuggestionAdminStore` (`Infrastructure/Skills/SkillSuggestionAdminStore.cs`). Домен: `SkillSuggestion.Approve/Merge/Reject` (`Pending` → итог, повторно 409 `invalid_state_transition`; кто и когда решил — `ResolvedById` и `UpdatedAt`, новых колонок нет, миграции не нужны), `Skill.AddSynonym` (нижний регистр, без повторов и совпадений с названиями, не больше 20). Каждое решение: транзакция под блокировкой админа и автора (решения по предложениям одного автора идут по очереди), общий `TrySaveAsync` каталога (уникальный индекс названия: гонка двух админов даёт 409 `skill_name_taken`), аудит (`SkillSuggestionApproved`, `SkillSuggestionMerged`, `SkillSuggestionRejected`, цель `SkillSuggestion`), уведомление автору, `Dispatch()` после коммита.
- **Дубли.** `FindPendingSameAsync`: остальные неразобранные с тем же `NormalizedName` в той же (предложенной) категории объединяются с тем же навыком в той же транзакции (аудит с `duplicateOf`, уведомление `SkillSuggestionMerged`). Отказ дублей не трогает. `similar` в очереди: `ILIKE` по трём названиям плюс точное совпадение синонима, до 5, сначала включённые.
- **Каталог и аудит.** `SkillCatalogAdminService`: изменяющие методы принимают `adminId`, проверяют по базе действующего админа или суперадмина (раньше хватало роли в токене, и снятый админ мог править каталог до конца срока токена) и пишут аудит (`CategoryCreated/Updated/Deleted`, `SkillCreated/Updated/Deleted`) в той же записи, что и изменение. Тесты: `SkillSuggestionsAdminTests` (11), `SkillSuggestionResolutionTests` (домен); `AdminSkillCatalogTests` теперь берут настоящего админа.

## Модераторы админа и освобождение обращений (этап 7, шаг 4): для бэкенда

Контракт для фронтенда: `docs/FRONTEND-ADMIN.md`, раздел 5 (тег **Admin moderators**, маршруты под `/admin/staff/moderators`, потому что `GET /admin/moderators` уже занят выбором проверяющего).

- **Один путь освобождения.** `Staff/ModeratorCaseReleaser` используют снятие модератора и привязка личного аккаунта. Порядок как у `AccountBlocker`: `UsersToLockAsync` (модератор и участники всех обращений, которые он держит), блокировка их, `ReleaseAsync` в той же транзакции, сохранение и `Dispatch()` после коммита делает вызывающий. `ICaseStore.FindHeldByModeratorAsync` (с фильтром `conflictUsers` или без) возвращает взятые им обращения (`InProgress`, `WaitingResponse`) и апелляции, где он проверяющий (`AppealReview`). Обращение: `Case.ReleaseFromModerator` (в `Open`, исполнитель, срок ответа и напоминание сбрасываются, `DueAt` остаётся), апелляция: `CaseAppeal.ClearReviewer` плюс `Case.EndAppealReview` (снова `Appealed`). Доступы к чату по обращению отзываются через `ModerationService.RevokeChatAccessAsync` (аудит `ChatAccessRevoked` на каждый), аудит `CaseReleased` или `AppealReviewReleased` с причиной `staff_removed` или `conflict_of_interest`. Задачи Hangfire по сроку ответа и напоминанию переживают возврат: они сверяют статус и срок и ничего не делают.
- **Снятие** (`ModeratorAdminService.RemoveAsync`): проверки (`StaffRules.RequireNotSelf`, `CanManage`, только роль модератора, ещё действующий), причина 10–500 символов, пароль и код 2FA (`StepUpService`), затем одна транзакция: блокировка пользователей, `User.RemoveFromStaff` (Active → Blocked, роль остаётся), отзыв refresh-токенов, освобождение обращений, `PersonalLinkService.UnlinkForRemovalAsync` (снятие привязки в той же транзакции без второго подтверждения), аудит `StaffRemoved` (причина, `casesReturned`, `appealReviewsReturned`, `personalUnlinked`). После коммита уведомления и разрыв SignalR (`IRealtimeDisconnector`). Письма снятому нет. **Снятие админа** (`RemoveAdminAsync`, тот же `RemoveCoreAsync`, `POST /admin/staff/admins/{id}/remove`): вызывающий обязательно суперадмин (`StaffRules.CanManage`: админ админа и суперадмина не снимает никто), вместо освобождения обращений `ModeratorCaseReleaser.ReleaseAdminAppealsAsync` очищает `CaseAppeal.AdminId` (`CaseAppeal.ReleaseAdmin`) у апелляций без решения, аудит `AppealAdminReleased`, остальным админам `CaseAppealed` для тех, что в `Appealed`; `AppealService` при заключении шлёт `AppealConclusionReady` всем админам, если ведущего нет. В аудите `StaffRemoved` добавлено `appealsReturnedToAdmins`. Тесты: `AdminRemovalTests` (4).
- **Привязка** (`PersonalLinkService.ConfirmAsync`): под блокировкой берутся и участники обращений сотрудника; после записи истории привязок считается конфликт интересов и `ReleaseAsync` освобождает обращения с конфликтом (исполнитель в аудите пустой). `conflictingCases` в уведомлении и аудите равно числу освобождённых. `GET /admin/staff/{id}/personal-links` работает и для снятого сотрудника.
- **Список.** `IModeratorStore` (`Infrastructure/Staff/ModeratorStore.cs`): модераторы с `AccountType = Staff`, `removed` — статус не `Active`; нагрузка тремя группировками на страницу. Тесты: `ModeratorsApiTests` (9), автоосвобождение в `PersonalLinkApiTests`, `CaseReleaseTests` (домен).

## Пользователи админа и общая блокировка (этап 7, шаг 3): для бэкенда

Контракт для фронтенда: `docs/FRONTEND-ADMIN.md`, раздел 4 (тег **Admin users**).

- **Один путь блокировки.** `Staff/AccountBlocker` используют и одобрение запроса на бан (`BanRequestService.ApproveAsync`), и ручная блокировка (`AdminUserService.BlockAsync`), чтобы последствия не расходились. Порядок: `UsersToLockAsync` (пользователь и партнёры по его открытым обменам), блокировка их в транзакции, `ApplyAsync` (`User.Block`, отзыв refresh-токенов, ожидающие заявки в обе стороны в `Expired`, обмены в `Aborted` с `account_blocked`, партнёру `ExchangeAborted` с `partner_unavailable`, отмена уроков, аудит `UserBlocked` с источником `manual` или `ban_request`), после коммита `FinishAsync` (рассылка уведомлений, письмо, `IRealtimeDisconnector`, карточки уроков, приглашения на отзыв). Любое новое последствие блокировки добавляется только сюда.
- **Ручная блокировка** дополнительно закрывает все необработанные запросы на бан по пользователю как одобренные админом (аудит `BanDecided`, `BanRequestDecided` запросившему) и блокирует в транзакции и запросивших. Служебные аккаунты и себя заблокировать нельзя.
- **Разблокировка** (`User.Unblock`: Blocked → Active): обмены не возвращаются, аудит `UserUnblocked`, письмо `EmailTemplates.AccountUnblocked` (tg, ru, en).
- **Снятие предупреждения** (`Warning.Revoke`): аудит `WarningRevokedManually`, `WarningRevoked` с `manual: true`, автоматический запрос на бан (`RequestedById == null`) отклоняется, если действующих меньше `Warning.BanThreshold`.
- **Чтение.** `IAdminUserStore` (`Infrastructure/AdminUsers`): поиск `ILIKE` с экранированием спецсимволов, счётчики предупреждений и обращений одним запросом на страницу, события блокировки из `AuditLogs` (по `UserBlocked`, `UserUnblocked`). Миграции не нужны. Тесты: `AdminUsersApiTests` (13), `UserBlockTests`.

## Привязка личного аккаунта (этап 7, шаг 2): для бэкенда

Контракт для фронтенда: `docs/FRONTEND-ADMIN.md`, раздел 3 (тег **Personal account link**). `PersonalLinkService`, сущность `PersonalLinkRequest` (таблица `PersonalLinkRequests`: `StaffId`, `PersonalUserId`, `TokenHash` (SHA-256, уникален), `ExpiresAt` (24 часа), `ConfirmedAt`, `DeclinedAt`, `CancelledAt`; состояние вычисляется).

- **Доказательство владения.** Письмо со ссылкой уходит на почту личного аккаунта (токен только в письме), а подтвердить может только вошедший в этот личный аккаунт: чужой токен для другого пользователя выглядит как несуществующий (404). Служебный аккаунт подтвердить не может.
- **Запрос.** Подтверждение паролем и кодом 2FA, потом проверки: личный аккаунт найден через Identity, обычный, действующий, с подтверждённой почтой (иначе единый 404, чтобы не различать причины); у сотрудника ещё нет привязки; личный не привязан к другому служебному; не больше 5 запросов в час (считаются по сроку запроса, он ставится по часам приложения). Прежние открытые запросы сотрудника отменяются новым, в одной транзакции под блокировкой обоих аккаунтов.
- **Подтверждение.** Транзакция под блокировкой обоих аккаунтов и `SELECT ... FOR UPDATE` по запросу: повторная проверка «не привязан» (гонка двух сотрудников с запросами на один личный аккаунт: проходит первый, второй получает 409), `User.LinkPersonalAccount`, аудит `PersonalAccountLinked` (исполнитель — владелец личного аккаунта), уведомление сотруднику. В уведомлении `conflictingCases`: сколько обращений сотрудника в работе (взятые им и повторные проверки по апелляции) теперь с конфликтом интересов; эти обращения освобождаются тут же в той же транзакции (раздел «Модераторы», шаг 4).
- **Снятие и история (закрытая лазейка).** Каждая подтверждённая привязка пишется в **историю** (`StaffPersonalLinks`: `StaffId`, `PersonalUserId`, `LinkedAt`, `UnlinkedAt`, `UnlinkedById`; записи не удаляются). Проверка конфликта интересов (`ICaseStore.GetConflictUserIdsForStaffAsync`) берёт **все** личные аккаунты, когда-либо привязанные к сотруднику (история плюс текущая `LinkedUserId`), и всех, с кем они вели обмен: снятие привязки конфликт не убирает, привязать тот же аккаунт обратно ничего не меняет. Сам сотрудник привязку снять не может: он отправляет **запрос на снятие** (`PersonalUnlinkRequests`, причина 10–500 символов, не больше одного необработанного), решает админ (модераторов) или суперадмин (админов и других суперадминов; `StaffRules.CanUnlinkPersonal`), себе никто (`cannot_change_own_rights`), с подтверждением паролем и кодом 2FA. Одобрение и прямое снятие (`POST /admin/staff/{id}/personal-link/unlink`) идут одним методом: транзакция под блокировкой админа, сотрудника и личного аккаунта, запись истории закрывается (если привязка была создана в обход подтверждения, историю дописывает само снятие), необработанный запрос закрывается как одобренный, аудит `PersonalAccountUnlinked` с исполнителем-админом, уведомление сотруднику. Миграция `AddStaffPersonalLinkHistory` переносит уже существующие привязки в историю.
- **Эффект.** Конфликт интересов считается динамически по истории привязок и обменам личных аккаунтов (раздел «Модератор»): после привязки обращения с личным аккаунтом и его бывшими партнёрами пропадают из очереди, `take` даёт 403 `conflict_of_interest`; **после снятия привязки они остаются недоступными** (проверено сквозными тестами, в том числе «снял привязку, взял обращение бывшего личного аккаунта»).

## Приглашения служебных аккаунтов (этап 7, шаг 1): для бэкенда

Контракт для фронтенда: `docs/FRONTEND-ADMIN.md` (тег **Staff invitations**). `StaffInvitationService`: создание, список, отзыв, повторная отправка, просмотр и приём по ссылке.

- **Токен.** 256 случайных бит (base64url), в базе только SHA-256 в hex (`StaffInvitationTokens`); токена нет ни в ответах API, ни в журнале, он есть только в письме. Ссылка собирается из `Frontend:BaseUrl` и `Frontend:StaffInvitationPath` (по умолчанию `/staff/invitation/{token}`).
- **Создание.** Сначала проверка роли (`StaffRules`: админ приглашает модераторов, суперадмин ещё и админов), потом подтверждение паролем и кодом 2FA (чтобы отказ по роли не сжигал код), потом адрес: занятый Identity-адрес даёт 409 `email_already_registered`, открытое (действующее или истёкшее) приглашение на адрес даёт 409 `invitation_already_exists`. Письмо уходит после записи (если отправка упала, приглашение остаётся, его отправляют заново). Язык письма: `language` из запроса, иначе язык пригласившего (tg, ru, en).
- **Приём.** Один транзакционный проход с `SELECT ... FOR UPDATE` по строке приглашения: проверка состояния, создание служебного аккаунта через Identity (почта подтверждена приглашением, 2FA ещё нет), `Accept`, аудит `StaffInvitationAccepted` (исполнитель — новый аккаунт), уведомление `StaffInvitationAccepted` пригласившему. Две одновременные попытки с одной ссылкой: одна проходит, вторая получает 409 `invitation_already_used`; если адрес заняли между приглашением и приёмом, 409 `email_already_registered` и приглашение остаётся действующим. Ответ `requires2faSetup` + `twoFactorToken` совпадает с ответом входа служебного без 2FA, дальше обычные `/auth/2fa/setup` и `/auth/2fa/verify`. Маршруты приёма без входа ограничены тем же лимитом по адресу клиента, что и вход.
- **Аудит.** `StaffInvited`, `StaffInvitationRevoked` (только если отзыв состоялся), `StaffInvitationResent`, `StaffInvitationAccepted`; значения пароля и кода в журнал не попадают.
- **Сидер разработки.** `POST /dev/seed-staff` теперь создаёт служебные аккаунты с **известным паролем** (`Dev-staff-pass-1`) и **включённой 2FA с известным ключом**, возвращает ключ, текущий и следующий код и резервные коды (при включении); `POST /dev/staff-code { email }` возвращает текущий и следующий код когда угодно. Аккаунты, созданные прежней версией сидера (случайный пароль, без 2FA), при следующем вызове приводятся к этому состоянию.

## Панели и служебные аккаунты (этап 7, шаг 0): основа

Решения для этапа (согласованы 2026-11-21): подтверждение паролем и кодом 2FA **на каждое** действие по ролям и системным настройкам (окна «подтверждено недавно» нет); статистика минимальным набором; разблокировка прерванные обмены не возвращает; ручная блокировка админом даёт те же последствия, что одобренный бан (один общий метод, шаг 3).

**Приглашение (`StaffInvitation`, таблица `StaffInvitations`).** `Email` (нижний регистр), `TargetRole` (`Moderator` или `Admin`; суперадмина по приглашению не создать), `InvitedById`, `TokenHash` (SHA-256 токена из ссылки, уникален; сам токен нигде не хранится), `ExpiresAt` (72 часа), `AcceptedAt`, `AcceptedUserId`, `RevokedAt`, `ResendCount`. Состояние вычисляется: `Pending`, `Accepted`, `Revoked`, `Expired`. Принять можно только действующее (ошибки `invitation_expired`, `invitation_revoked`, `invitation_already_used`); отозвать нельзя принятое; «отправить заново» даёт новый токен и новые 72 часа, прежняя ссылка перестаёт работать (можно для действующего и истёкшего). Эндпоинты приглашений: шаг 1.

**Правила ролей (`StaffRules`).** Модераторов приглашает и снимает админ или суперадмин, админов только суперадмин (403 `staff_role_not_allowed`); никто не меняет права себе (403 `cannot_change_own_rights`); последнего действующего суперадмина нельзя снять или заблокировать (409 `last_superadmin`). Сервисы шагов 1–4 вызывают эти проверки под блокировкой нужных строк.

**Подтверждение действия (`StepUpService`).** В теле каждого запроса, который меняет роли или системные настройки, приходит `confirmation: { password, code, isRecoveryCode }`. Проверяется пароль и второй фактор (код приложения или резервный; использованный код сгорает, тем же кодом второе действие не подтвердить). Ошибки: 400 `step_up_required` (нет пароля или всего объекта), 400 `invalid_password`, 400 `two_factor_code_required`, 400 `invalid_two_factor_code`, 409 `two_factor_not_enabled` (у служебного аккаунта нет 2FA), 403 `staff_required` (не служебный, не действующий или нет такого), 429 `account_locked_out` (серия неудач блокирует на 15 минут, как при входе). Каждая неудача пишется в аудит (`StepUpFailed`, причина, без значений пароля и кода). Названия действий для журнала аудита этапа 7: `StaffAuditActions`.

## Этап 6: итог (отзывы и обращения)

Сделано строго по разделам 8.1–8.3, 9.4 и 10.2 ТЗ; подробности по шагам выше, сводка для фронтенда: `docs/FRONTEND-CASES.md`.

| Требование ТЗ | Где реализовано | Чем проверено |
| --- | --- | --- |
| 8.1: слепые взаимные отзывы, 4 критерия и текст, срок 14 дней, одностороннее право при неявках, рейтинг и «Новичок» | `ReviewRules`, `ReviewService`, `ReviewJobs` | `ReviewRulesTests`, `ReviewsApiTests` |
| 8.2: три типа обращений, окна подачи, доказательства, лимиты, заморозка отзыва и обмена (жалоба из звонка), отзыв до взятия | `CaseService`, `CaseRules` | `CasesApiTests` |
| 8.2 п. 2 и 10.2: доступ к чату только после взятия, на этот обмен, только чтение, аудит каждого запроса, одно уведомление, конфликт интересов, админ чат не читает | `ModerationService`, `ChatAccessPolicy`, `AuditLogs` (триггер БД) | `ModeratorCasesTests` (в том числе IDOR) |
| 8.2 п. 3: пояснения, 48 часов, возврат в работу, напоминание, досрочное завершение | `ExplanationService`, `CaseJobs` | `ExplanationsTests`, `AppealsTests` |
| 8.2 п. 4 и 8.3: решения и их последствия, отзыв доступа сразу, письма tg/ru/en, закрытие через 7 дней | `ResolutionService`, `CaseConsequences` | `ResolutionTests` (в том числе сквозной: чат закрыт после решения) |
| 8.2 п. 5: апелляция один раз, админ без чата, другой модератор с новым доступом, окончательное решение с откатом, блокировка по запросу | `AppealService`, `AdminCaseService`, `BanRequestService` | `AppealsTests` (в том числе сквозной: проверяющий теряет доступ после заключения) |
| 10.3: очистка чатов через 90 дней, защита доказательств (год), страховочная сверка, удержание данных удалившихся | `ChatCleanupJobs`, `CaseMaintenanceJobs` | `MaintenanceTests` |
| Контракт для фронтенда, сроки для ручной проверки, сидеры | `FRONTEND-CASES.md`, `CaseTiming`, `CaseSeedDevTools` | `FrontendDocsTests`, `CaseSeedDevToolsTests` |

**Сквозной прогон на настоящем стеке** (Development, PostgreSQL, Hangfire, Mailpit, сокращённые сроки; 36 проверок, все пройдены): подача и взятие (по одному `CaseTaken`), чтение чата (3 сообщения, 2 доказательства, `ChatRead` в аудите, уведомлений не появилось, админу 403), вопрос обоим, решение во время ожидания запрещено, ответы вернули обращение в работу (`CaseReturnedToWork`), решение (чат 403, `ChatAccessRevoked`, `CaseResolved` и письма), апелляция, материалы админа (2 доказательства, остального чата нет), назначение проверяющего (по одному `CaseAppealReviewAssigned`), чтение чата проверяющим, заключение (чат 403, `ChatAccessRevoked`), отмена решения (предупреждение отозвано, письмо), заморозка с запросом на бан и его одобрение (статус `Blocked`, вход закрыт 403 `account_blocked`, живой токен 403, обмен `Aborted` с причиной `partner_unavailable`, письмо заблокированному), спор по отзыву (заморозка и возврат в рейтинг), закрытие обращения задачей Hangfire через сокращённый срок апелляции. Не проверялось руками (покрыто тестами): очистка чатов через 90 дней (нужно время), отключение SignalR (проверено на подставном соединении), письма на таджикском (нет вычитки носителем).

**Что переходит в этап 7:** приглашения персонала (`StaffInvitation`), панель админа (снятие блокировки, управление ролями), панель Hangfire для суперадмина, права БД для `AuditLogs` на боевой базе, очистка старых уведомлений, сверка бакета с таблицей вложений. Полный список с оговорками: `docs/DEFERRED.md` (записи «Этап 6, шаг N»).

**Что обновить в ТЗ** (решения этапа 6, которых там нет): решение `NoViolation`; предупреждение сочетается с `ReviewHidden`, `ExchangeEnded`, `FrozenBanRequested`; досрочное завершение ожидания пояснений с причиной; отмена решения админом `Overturned` (для спора возвращается `ReviewKept`, для жалоб `NoViolation`); статус обращения `Withdrawn`; таблицы `Cases` (`DueAt`, `OverdueMarkedAt`, `ExplanationReminderSentAt`), `Exchanges` (`ChatPurgedAt`, причина прерывания `account_blocked`); сроки обращений настраиваются в Development и Testing; блокировка по запросу (минимальная); миграции в Development применяются при запуске.

## Отзывы и обращения: основа (этап 6, шаг 0): для бэкенда

Фронтенду здесь ничего вызывать не нужно: API отзывов и обращений появится в следующих шагах. Что заложено.

- **Таблицы:** `Reviews`, `Cases`, `CaseEvidence`, `CaseMessages`, `CaseAppeals`, `ChatAccessGrants`, `Warnings`, `BanRequests`, `AuditLogs`. Отличия от раздела 13 ТЗ: у `Cases` есть `DueAt` (срок реакции 24 или 72 часа), `Category`, `Description`, `FromCall`, `AlsoWarned`, `WasAppealed`; у `Warnings` есть `RevokedAt` (предупреждение по отменённому решению); у `BanRequests` `RequestedById` может быть пустым (запрос создала система после трёх предупреждений); у `CaseMessages` есть `InReplyToId` (ответ на вопрос модератора).
- **Статусы** хранятся текстом. Обращение: `Open`, `Withdrawn`, `InProgress`, `WaitingResponse`, `Resolved`, `Appealed`, `AppealReview`, `Closed` (раздел 9.4 ТЗ, переходы только методами `Case`). Отзыв: `Pending`, `Published`, `Hidden`, `Frozen`.
- **Решения по типам.** Спор по отзыву: `ReviewKept`, `ReviewHidden`. Жалоба на поведение и неявки: `Warning`, `ExchangeEnded`, `FrozenBanRequested`, `NoViolation`. К `ReviewHidden`, `ExchangeEnded` и `FrozenBanRequested` можно добавить предупреждение (`AlsoWarned`).
- **Сроки (`CaseRules`):** подача спора 14 дней после публикации отзыва, неявок 30 дней после обмена, жалобы на поведение 90 дней; реакция 24 часа (высокий приоритет) или 72 часа; ответ на вопрос 48 часов; апелляция и закрытие без неё 7 дней после решения. Высокий приоритет: жалоба на поведение, участие пользователя 16–17 лет, жалоба из звонка.
- **Служебные аккаунты и доступ.** Политики в `SkillSwap.Api.Auth.Policies`: `Moderator` (только модератор), `Admin` (админ и суперадмин), `Staff` (любая служебная роль). `User.LinkPersonalAccount` привязывает личный аккаунт сотрудника (`LinkedUserId`, не больше одного служебного на личный): по нему проверяется конфликт интересов. Заводить модераторов и админов в этапе 6 нечем (приглашения — этап 7): в тестах это `ModerationTestData.StaffAsync`, в Development появится отдельный сидер.
- **Чат читают только участники и модератор с активным доступом.** `ChatAccessPolicy` (и ресурсный обработчик `ChatAccessHandler`, ресурс — id обмена): участник обмена или модератор с неотозванным `ChatAccessGrant` именно на этот обмен. Админ и суперадмин не читают чат никогда. Доступ создаётся при взятии обращения в работу и отзывается при решении (следующие шаги); один активный доступ на обращение и модератора гарантирует индекс БД.
- **Журнал аудита только на добавление.** `IAuditLog.Add` (в той же транзакции, что действие) и `WriteAsync` (сразу, для чтения истории чата). Базу защищает триггер: UPDATE, DELETE и TRUNCATE по `AuditLogs` отклоняются (`insufficient_privilege`), даже от владельца таблицы. Адрес клиента берётся из запроса (`IRequestInfo`; IPv4 в виде IPv6 разворачивается). Названия действий — `AuditActions`.
- **Доказательства не пропадут.** Внешний ключ `CaseEvidence → Messages` запрещает удалить сообщение, приложенное к обращению: очистка чата упрётся в него, а не потеряет сообщение (раздел 10.3 ТЗ).
- **Конкурентность.** `Cases`, `Reviews`, `CaseAppeals`, `BanRequests` защищены `xmin`: два модератора не возьмут одно обращение, два админа не решат один запрос на бан, публикация и заморозка отзыва не пройдут обе (второй получает конфликт).

## Тесты

```
cd backend
dotnet test
```

Интеграционные тесты сами поднимают PostgreSQL в Docker (Testcontainers), Docker должен быть запущен.

Замер производительности подбора (раздел 14 ТЗ, p95 не больше 300 мс) входит в обычный прогон: тест `MatchPerformanceTests` создаёт 5000 пользователей в отдельном контейнере БД. Для проверки запаса: `SKILLSWAP_PERF_USERS=20000 dotnet test --filter MatchPerformanceTests` (PowerShell: `$env:SKILLSWAP_PERF_USERS='20000'`).

То же самое на каждый push в main делает CI (`.github/workflows/backend-ci.yml`: restore, сборка в Release, все тесты на Ubuntu с Docker, .NET 10). Секретов он не требует: тесты сами создают временные ключи и сертификат. Результаты (`.trx`) сохраняются как артефакт запуска на 7 дней. Если CI красный, а локально зелёный, сначала смотрите различия Windows/Linux (регистр в путях, переводы строк).

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
pre-join, таймер, автофиксация урока) — этап 5. Уроки (`LessonSession`, таблица `Sessions`) уже есть (раздел «Уроки»), к комнате они пока не привязаны.

**Запуск.**

1. В `backend/.env` задайте `LIVEKIT_API_SECRET`: случайная строка **от 32 символов** (в `.env.example` есть шаблон). Это секрет ключа `devkey` локального LiveKit.
2. То же значение задайте приложению: `dotnet user-secrets set "LiveKit:ApiSecret" "<значение>" --project src/SkillSwap.Api`.
3. Поднимите медиасервер: `docker compose up -d livekit` (образ `livekit/livekit-server:v1.13.11`, режим `--dev`, настройки в `backend/livekit.yaml`).
4. Запустите API в профиле **http**: `dotnet run --project src/SkillSwap.Api --launch-profile http` (порт **5023**: на него в `livekit.yaml` указан webhook).

Серверный API LiveKit (удаление комнаты при закрытии окна урока): адрес берётся из `LiveKit:Url` (`ws` → `http`, `wss` → `https`), при необходимости задайте свой `LiveKit:ApiUrl` (например, внутренний адрес контейнера). `LiveKit:ApiTimeoutSeconds` (10) — сколько ждать ответ. Если LiveKit не настроен, комнаты не удаляются, а итог урока всё равно подводится.

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

Записанное событие тут же учитывается в посещаемости урока (см. «Присутствие в комнате»); `ProcessedAt` показывает, что оно учтено.

**Ограничения локального стенда.**
- Звонок проверяется **только с этого компьютера**: в `livekit.yaml` задан `node_ip: 127.0.0.1`. Для телефона в той же сети замените его на LAN-адрес компьютера.
- Webhook идёт из контейнера на `host.docker.internal:5023`. На Windows и macOS этот адрес есть сам, на Linux он добавлен в `docker-compose.yml` через `extra_hosts`.
- В Development webhook по обычному `http` не перенаправляется на `https` (иначе самоподписанный сертификат ломал бы доставку); во всех остальных средах редирект работает как обычно.
- Проверка через мобильных операторов и TURN на 443/TLS на VPS отложена (`docs/DEFERRED.md`).

**Автотесты.** Приём webhook, выдача токена и страница покрыты тестами на настоящей БД; Cloudflare-подобной заглушки здесь нет: подпись формируется как у LiveKit (формат снят с настоящего сервера).
Сам звонок автотестами не проверяется (нужен браузер и контейнер LiveKit): его проверяют вручную по описанию выше.
