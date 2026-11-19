# Ответы бэкенда на вопросы фронтенда

Схема всего API: [`openapi.json`](openapi.json) (обновляется в каждом шаге, где меняется API; CI проверяет, что файл не устарел). Правила и коды ошибок реализованных эндпоинтов: `BACKEND-DEV.md`.

Файла с вопросами (`frontend/BACKEND-QUESTIONS.md`) в репозитории пока нет, поэтому ниже только ответ, о котором известно из заявки. Остальные вопросы добавятся сюда после появления файла.

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

Отличия от прикидочной формы: статус урока `Scheduled` (а не `Confirmed`), добавлены `endAt` и `canJoinUntil`, у `partner` есть `isDeleted`; в `actionsNeeded` пока нет `WriteReview` (появится вместе с отзывами), зато есть `AnswerRequest`, `AnswerSession`, `ConfirmSession`, `FinishExchange`, `AnswerExtension`. Вход в видеокомнату (`canJoinFrom`) заработает на этапе 5.
