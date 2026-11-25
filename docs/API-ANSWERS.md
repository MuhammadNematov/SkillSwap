# Ответы бэкенда на вопросы фронтенда

Схема всего API: [`openapi.json`](openapi.json) (обновляется в каждом шаге, где меняется API; CI проверяет, что файл не устарел). Правила и коды ошибок реализованных эндпоинтов: `BACKEND-DEV.md`.

Файл `frontend/BACKEND-QUESTIONS.md` описывает связку фронтенда с бэкендом; ниже ответы на конкретные вопросы по мере их появления.

## Вопрос 14. Главная страница: ближайший урок и статистика

**Готово: `GET /me/dashboard`.** Один запрос вместо нескольких, нужен вход, только обычный аккаунт (служебному 403 `regular_account_required`). Все даты в UTC. Подробности и правила: раздел «Главная страница» в `BACKEND-DEV.md`.

```json
{
  "nextSession": {
    "id": "…", "exchangeId": "…", "startAt": "2026-12-01T13:00:00Z", "endAt": "2026-12-01T13:45:00Z", "durationMin": 45, "topic": "…",
    "status": "Scheduled", "iAmTeacher": true,
    "partner": { "id": "…", "name": "…", "avatarUrl": null, "isDeleted": false },
    "canJoinFrom": "2026-12-01T12:50:00Z", "canJoinUntil": "2026-12-01T14:15:00Z"
  },
  "counters": { "activeExchanges": 2, "incomingRequests": 1, "outgoingRequests": 0, "unreadMessages": 5, "unreadNotifications": 3 },
  "stats": { "completedSessions": 12, "taughtMinutes": 540, "learnedMinutes": 300, "completedExchanges": 3 },
  "rating": { "average": 4.8, "count": 7, "isNewcomer": false },
  "actionsNeeded": [
    { "type": "ConfirmSession", "sessionId": "…", "exchangeId": "…", "requestId": null, "dueAt": "…" },
    { "type": "AnswerRequest", "sessionId": null, "exchangeId": null, "requestId": "…", "dueAt": "…" }
  ]
}
```

Отличия от прикидочной формы: статус урока `Scheduled` (а не `Confirmed`), добавлены `endAt` и `canJoinUntil`, у `partner` есть `isDeleted`; в `actionsNeeded` есть `WriteReview` (отзыв по закрытому обмену, `dueAt` — срок отзыва), а также `AnswerRequest`, `AnswerSession`, `ConfirmSession`, `FinishExchange`, `AnswerExtension`. Вход в видеокомнату (`canJoinFrom`) заработает на этапе 5.

## Вопросы этапа 5 (видеокомната, чат, список чатов)

Общий контекст: [`BACKEND-DEV.md`](BACKEND-DEV.md), раздел «Видеокомната: сводный контракт для фронтенда». Все поля в JSON в camelCase, перечисления строками, время в UTC (ISO 8601).

### 1. Перечисления в OpenAPI

**Исправлено.** В `openapi.json` у каждого перечисления теперь `type: string` и полный список значений (`AttachmentKind`: `File`, `Voice`, `Image`; `DataExportStatus`: `Pending`, `Processing`, `Ready`, `Failed`, `Expired`; так же статусы уроков, обменов, заявок, типы сообщений и уведомлений и т. д.). Причина была в том, что генератор схемы читал настройки JSON «минимальных» эндпоинтов, где строковый конвертер не был включён. Тест `Every_enum_in_the_schema_is_a_string_with_a_list_of_values_and_none_is_an_integer` (`OpenApiSnapshotTests`) проверяет, что ни одно перечисление не описано числом и что у каждого перечисления домена в схеме есть полный список значений. Если генератор клиентов вы запускали раньше, перегенерируйте типы.

### 2. Хаб `/hubs/chat`: методы и события

**Подключение.** `https://…/hubs/chat`, JWT: заголовок `Authorization: Bearer` или параметр `access_token` (браузерный WebSocket). Пример:

```ts
const connection = new HubConnectionBuilder()
  .withUrl(`${API_ORIGIN}/hubs/chat`, { accessTokenFactory: () => accessToken })
  .withAutomaticReconnect()
  .build()
connection.on('message', (m) => …)
await connection.start()
```

При подключении сервер сам добавляет пользователя в группы всех его обменов, которые ему видны, поэтому после переподключения `Join` повторять не нужно. `Join` нужен только для обмена, созданного уже после подключения. Для токена с истекшим сроком получите новый (`/auth/refresh`) и пересоздайте соединение.

**Методы, которые вызывает клиент** (`connection.invoke(name, …args)`; имена как здесь):

| Метод | Аргументы | Возвращает | Что делает |
| --- | --- | --- | --- |
| `Join` | `exchangeId: Guid` | — | войти в группу обмена |
| `Leave` | `exchangeId: Guid` | — | выйти из группы |
| `Send` | `exchangeId: Guid`, `text: string` | `MessageDto` | отправить текстовое сообщение (до 4000 символов) |
| `Edit` | `messageId: Guid`, `text: string` | `MessageDto` | **изменить** своё сообщение (в течение 15 минут) |
| `Delete` | `messageId: Guid` | `MessageDeletedDto` | **удалить** своё сообщение (в течение 15 минут) |
| `Read` | `exchangeId: Guid`, `upToMessageId: Guid \| null` | `ReadReceiptDto` | отметить прочитанным до сообщения включительно (`null` — всё) |
| `Typing` | `exchangeId: Guid` | — | «печатает…» (не чаще раза в 2 секунды с соединения; собеседнику, не себе) |

Ошибка метода приходит исключением `HubException`, в `message` лежит **машинный код**: `chat_read_only`, `chat_archived`, `exchange_not_found`, `message_not_found`, `not_message_author`, `edit_window_expired`, `message_deleted`, `message_empty`, `message_too_long`, `chat_rate_limited` (лимит 20 сообщений за 10 секунд) и др.; коды те же, что у REST (`BACKEND-DEV.md`, «Чат обмена»). Голосовые и файлы отправляются через REST (`POST /exchanges/{id}/attachments`), событие `message` для них приходит так же.

**События от сервера** (`connection.on(name, handler)`), всем в группе обмена, включая другие вкладки автора:

| Событие | Данные | Когда |
| --- | --- | --- |
| `message` | `MessageDto` | новое сообщение (текст, голосовое, файл, карточка урока) |
| `messageEdited` | `MessageDto` | сообщение изменено (`editedAt` заполнено) |
| `messageDeleted` | `{ exchangeId, messageId, deletedAt }` | сообщение удалено |
| `messagesRead` | `{ exchangeId, readerId, upToMessageId, marked, readAt }` | собеседник прочитал: две галочки у `marked` сообщений |
| `typing` | `{ exchangeId, userId }` | собеседник печатает (себе не приходит) |
| `sessionUpdated` | `LessonSessionDto` целиком (с `participantsInRoom`) | урок изменился: карточка в чате обновляется по `id`, плюс «партнёр в комнате» |

`MessageDto`: `id`, `exchangeId`, `senderId` (null — системное), `type` (`Text`, `Voice`, `File`, `SessionCard`, `System`), `text` (null у удалённого и у сообщений без текста), `paymentWarning` (в тексте найдены номер карты или призыв к оплате), `attachmentId`, `sessionId`, `createdAt`, `editedAt`, `deletedAt`, `readAt` (null — не прочитано), `attachment` (`{ id, kind, fileName, contentType, sizeBytes, durationSeconds }`, у удалённого нет), `session` (`LessonSessionDto` для `SessionCard`).

`LessonSessionDto`: `id`, `exchangeId`, `teacherId`, `startAt`, `endAt`, `durationMinutes`, `topic`, `status`, `proposedById`, `previousStartAt`, `cancelledById`, `confirmedBy` (список id), `unconfirmedAt`, `confirmDeadlineAt`, `createdAt`, `noShowUserId`, `lateJoinMinutes`, **`participantsInRoom`** (список id пользователей, сейчас в комнате; пусто, если никого).

Хаб **`/hubs/notifications`** (тот же способ подключения): клиент ничего не вызывает, сервер присылает событие **`notification`** с `{ id, type, payload, isRead, createdAt, readAt }` (как элемент `GET /notifications`); получатель — только владелец уведомления, на все его вкладки.

### 3. `lastMessage` в карточке обмена

**Добавлено** в `GET /exchanges` и `GET /exchanges/{id}`: `lastMessage` = `{ id, type, preview, createdAt, senderId, isDeleted }` или `null` (в чате пусто). `preview` до 100 символов: длиннее — 99 знаков и «…»; переводы строк и повторные пробелы сжимаются в один пробел; эмодзи не разрываются. У голосового, файла, карточки урока и **удалённого** сообщения `preview: null`, подпись по `type` (например «Голосовое сообщение») строит фронтенд; у удалённого `isDeleted: true`. Текст сообщений хранится зашифрованным, превью расшифровывается при выдаче, в базе открытого текста нет. Рядом в обмене: `unreadMessages` (непрочитанные), по `lastMessage.createdAt` можно сортировать список чатов.

### 4. LiveKit `url` и проверка с телефона

`url` в ответе `POST /sessions/{id}/join-token` берётся из настройки `LiveKit:Url` (на боевом сервере `wss://…`). В Development фронтенд это поле не использует: он подключается к `wss://{адрес страницы}/livekit` через прокси Vite, поэтому адрес не зависит от IP. Как заменить `127.0.0.1` на LAN-адрес для проверки с телефона (`node_ip` в `livekit.yaml`, `LiveKit:Url`, `Frontend:BaseUrl`, `--urls http://0.0.0.0:5023`, `npm run dev -- --host`, брандмауэр) и почему камере на телефоне нужен HTTPS (туннель или флаг Chrome) — пошагово в `BACKEND-DEV.md`, подраздел «Проверка видеозвонка с телефона в одной сети».

### 5. Кто сейчас в комнате (`participantsInRoom`)

**Добавлено**: в `sessionUpdated` и в ответах `GET /sessions/{id}`, `GET /exchanges/{id}/sessions` есть `participantsInRoom` — id пользователей, которые сейчас находятся в видеокомнате (по событиям LiveKit). «Партнёр в комнате» = `participantsInRoom` содержит id партнёра. Событие `sessionUpdated` приходит **при каждом входе и выходе** (не только при смене статуса), а также при закрытии комнаты (тогда список пуст). Переподключение участника не задваивает его: один человек — один раз. В карточках урока внутри истории чата (`GET /exchanges/{id}/messages`) и в календаре поле пустое: актуальное значение приходит событием или `GET /sessions/{id}`.

### 6. CORS

**Проверено и покрыто тестами** (`CorsTests`): адрес `Frontend:BaseUrl` (локально `http://localhost:5173`) получает `Access-Control-Allow-Origin` с точным адресом и `Access-Control-Allow-Credentials: true` для REST (обычные запросы, предварительные `OPTIONS` для всех методов и заголовков `authorization`, `content-type`, `accept-language`, ошибки 401/409 тоже) и для SignalR (`/hubs/chat/negotiate`, `/hubs/notifications/negotiate`, заголовок `x-signalr-user-agent`). Заголовок `Retry-After` доступен фронтенду (`Access-Control-Expose-Headers`). Другой адрес (другой порт, `https`, `null`) разрешения не получает. WebSocket передаёт токен параметром `access_token`. Если фронтенд открывается не на 5173, измените `Frontend:BaseUrl` бэкенда.

### 7. Ручная проверка видеокомнаты (только Development)

**Сделано**: `POST /dev/seed-lesson` и сокращённые сроки урока в `appsettings.Development.json`. Эндпоинт создаёт двух пользователей (с паролями в ответе), обмен и подтверждённый урок через 2 минуты, срок урока 5 минут, вход открыт сразу, закрытие комнаты через 3 минуты после окончания. Вне Development маршрута нет (404), в `openapi.json` его нет. Пошаговая инструкция, таблица сроков и сценарии проверки — в `BACKEND-DEV.md`, подраздел «Ручная проверка видеокомнаты без ожидания часов».
