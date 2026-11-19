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

3. Запустите API: письма появляются в Mailpit примерно через секунду. Панель фоновых задач (только в Development): http://localhost:5023/hangfire.

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
| `POST /exchanges/{id}/abort` | Выйти досрочно: тело `{ "reason": "…" }` обязательно (до 500 символов). `Aborted`, `abortedBy: "me"` |
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
- **`actionsNeeded`** — что ждёт ответа, **самое срочное сверху** (по сроку `dueAt`), каждого вида не больше 20. Типы: `AnswerRequest` (`requestId`, `dueAt` — срок ответа), `AnswerSession` (партнёр предложил время: `sessionId`, `exchangeId`, `dueAt` — начало урока), `ConfirmSession` (урок закончился, я не нажал «Урок состоялся»: `sessionId`, `exchangeId`, `dueAt` — до какого времени можно), `FinishExchange` (партнёр нажал «Завершить», а я нет: `exchangeId`), `AnswerExtension` (партнёр согласился продлить: `exchangeId`). Уже сделанное, истёкшее и своё предложение (ответ ждёт партнёр) в списке не показываются. `WriteReview` появится вместе с отзывами.


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
