# Ответы бэкенда на вопросы фронтенда

Схема всего API: [`openapi.json`](openapi.json) (обновляется в каждом шаге, где меняется API; CI проверяет, что файл не устарел). Правила и коды ошибок реализованных эндпоинтов: `BACKEND-DEV.md`.

Файла с вопросами (`frontend/BACKEND-QUESTIONS.md`) в репозитории пока нет, поэтому ниже только ответ, о котором известно из заявки. Остальные вопросы добавятся сюда после появления файла.

## Вопрос 14. Главная страница: ближайший урок и статистика

**Эндпоинта пока нет. Появится на этапе 4** (заявки, обмены, сессии), потому что считать его не из чего, пока нет обменов и сессий. Это `GET /me/dashboard`: один запрос вместо нескольких, доступен обычному аккаунту. До этапа 4 главная страница может показывать только данные профиля (`GET /me`) и подбор (`GET /matches`).

Примерная форма ответа (не окончательная, уточнится при реализации; даты в UTC):

```json
{
  "nextSession": {
    "id": "…", "exchangeId": "…", "startAt": "2026-12-01T13:00:00Z", "durationMin": 45, "topic": "…",
    "status": "Confirmed", "iAmTeacher": true,
    "partner": { "id": "…", "name": "…", "avatarUrl": null },
    "canJoinFrom": "2026-12-01T12:55:00Z"
  },
  "counters": { "activeExchanges": 2, "incomingRequests": 1, "outgoingRequests": 0, "unreadMessages": 5, "unreadNotifications": 3 },
  "stats": { "completedSessions": 12, "taughtMinutes": 540, "learnedMinutes": 300, "completedExchanges": 3 },
  "rating": { "average": 4.8, "count": 7, "isNewcomer": false },
  "actionsNeeded": [
    { "type": "ConfirmSession", "sessionId": "…" },
    { "type": "WriteReview", "exchangeId": "…", "dueAt": "…" }
  ]
}
```

- `nextSession` равен `null`, если ближайших сессий нет.
- `actionsNeeded` — то, что ждёт ответа пользователя («подтвердить слот», «оставить отзыв»).
- Счётчики и статистику считает сервер.
- Рейтинг уже сейчас приходит в `GET /me` (`ratingAvg`, `ratingCount`, `isNewcomer`).
