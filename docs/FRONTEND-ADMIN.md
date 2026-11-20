# Панели админа и суперадмина: контракт для фронтенда (этап 7)

Сводка растёт по шагам этапа. Точные схемы лежат в `docs/openapi.json` (теги **Staff invitations**, **Admin**), подробности для бэкенда: `docs/BACKEND-DEV.md`. Обращения, апелляции и запросы на блокировку: `docs/FRONTEND-CASES.md`.

## 1. Подтверждение действия паролем и кодом 2FA

Любое действие, которое меняет роли или системные настройки, подтверждается **на каждый запрос**: в теле `confirmation: { password, code, isRecoveryCode }`. Пароль служебного аккаунта; `code`: шесть цифр из приложения-аутентификатора либо резервный код (`isRecoveryCode: true`). Использованный код сгорает: тем же кодом второе действие не подтвердить (дождитесь следующего кода, до 30 секунд), окна «подтверждено недавно» нет. На форме подтверждения нужны два поля и переключатель «резервный код».

Ошибки подтверждения (одинаковые для всех таких маршрутов): 400 `step_up_required` (нет `confirmation` или пароля), 400 `invalid_password`, 400 `two_factor_code_required`, 400 `invalid_two_factor_code`, 409 `two_factor_not_enabled` (у аккаунта не включена 2FA), 403 `staff_required`, 429 `account_locked_out` (серия неудач блокирует на 15 минут). Каждая неудача пишется в аудит.

## 2. Приглашения служебных аккаунтов (шаг 1)

Тег **Staff invitations**. Модераторов приглашает админ или суперадмин, **админов только суперадмин**; суперадмина по приглашению не создать (403 `staff_role_not_allowed`). Перечисления строками: `role` — `Moderator` или `Admin`; `status` — `Pending`, `Accepted`, `Revoked`, `Expired`.

| Метод | Кто | Что делает |
| --- | --- | --- |
| `GET /admin/staff-invitations?status=&page=&pageSize=` | админ, суперадмин | Список, новые сверху. Админ видит приглашения модераторов, суперадмин все |
| `POST /admin/staff-invitations` | админ, суперадмин | Пригласить: `email`, `role`, `language` (tg, ru, en: язык письма, по умолчанию язык пригласившего), `confirmation`. 201 и приглашение |
| `POST /admin/staff-invitations/{id}/revoke` | админ, суперадмин | Отозвать (ссылка сразу не работает), тело `{ confirmation }` |
| `POST /admin/staff-invitations/{id}/resend` | админ, суперадмин | Отправить заново: новая ссылка и новые 72 часа, прежняя не работает; тело `{ confirmation, language }` (язык письма необязателен) |
| `GET /staff-invitations/{token}` | без входа | Что за приглашение (для формы): `email`, `targetRole`, `invitedByName`, `expiresAt` |
| `POST /staff-invitations/accept` | без входа | Принять: `token`, `name`, `password`, `uiLanguage` |

**Приглашение (`StaffInvitationDto`):** `id`, `email`, `targetRole`, `status`, `invitedBy` (`id`, `name`), `createdAt`, `expiresAt`, `acceptedAt`, `revokedAt`, `resendCount`. Токена в ответах нет: он приходит только в письме со ссылкой вида `{Frontend:BaseUrl}/staff/invitation/{token}` (путь настраивается: `Frontend__StaffInvitationPath`), срок 72 часа, ссылка одноразовая.

**Экран приёма.** Страница по ссылке сначала вызывает `GET /staff-invitations/{token}` и показывает адрес и роль (адрес менять нельзя), затем форма `name` (до 100 символов) и `password`. После `POST /staff-invitations/accept` создан отдельный служебный аккаунт; ответ как у входа служебного без 2FA: `requires2faSetup: true` и `twoFactorToken`. Дальше обычная настройка 2FA по этому токену: `POST /auth/2fa/setup` (QR-код и ключ), `POST /auth/2fa/verify` (первый код; в ответе резервные коды, покажите и попросите сохранить). Без 2FA работать нельзя.

**Ошибки.** Создание: 400 `invitation_email_invalid`; 409 `email_already_registered` (адрес уже занят), `invitation_already_exists` (на адрес уже есть открытое приглашение: отзовите или отправьте заново). Отзыв и повтор: 404 `invitation_not_found`; 409 `invalid_state_transition` (принятое отозвать, принятое или отозванное отправить заново нельзя). Просмотр и приём: 404 `invitation_not_found`; 409 `invitation_expired` (предложите попросить прислать заново), `invitation_revoked`, `invitation_already_used`, `email_already_registered`; 400 `invalid_name`, `password_required`, `weak_password`.

**Уведомление** `StaffInvitationAccepted` (пригласившему, только в интерфейсе): `invitationId`, `userId`, `userName`, `role`.

## 3. Привязка личного аккаунта (шаг 2)

Тег **Personal account link**. Привязка нужна, чтобы работал конфликт интересов (раздел 3 ТЗ, правило 3): модератор не видит и не берёт обращения, где участвует **любой личный аккаунт, когда-либо привязанный к нему**, или бывшие партнёры этих аккаунтов по обменам. Историю привязок хранит сервер и **снятие привязки конфликт не убирает** (иначе можно было бы снять привязку, взять обращение своего личного аккаунта и привязать обратно). Одновременно действует не больше одной привязки: один личный на служебный и один служебный на личный. Владение доказывается двумя вещами: письмом на почту личного аккаунта и входом в сам личный аккаунт.

**Снять привязку сам сотрудник не может**: он отправляет запрос с причиной, решает админ (у модераторов) или суперадмин (у админов и других суперадминов) с подтверждением паролем и кодом 2FA. Себе привязку не снимает никто.

| Метод | Кто | Что делает |
| --- | --- | --- |
| `GET /me/staff-link` | служебный | Состояние: `linked` (`userId`, `name`, `email`), `pending` (запрос на привязку: `id`, `personalEmail`, `expiresAt`), `unlinkRequest` (запрос на снятие: `id`, `reason`, `createdAt`); каждое может быть `null` |
| `POST /me/staff-link/request` | служебный | Попросить привязку: `email` личного аккаунта, `language` (tg, ru, en; язык письма, по умолчанию язык личного аккаунта), `confirmation`. Письмо уходит на почту личного аккаунта |
| `POST /me/staff-link/cancel` | служебный | Отменить действующий запрос на привязку |
| `POST /me/staff-link/unlink-request` | служебный | Попросить снять привязку: `reason` 10–500 символов |
| `GET /staff-link-requests/{token}` | личный аккаунт | Что за запрос: `staffName`, `staffRole`, `expiresAt` |
| `POST /staff-link-requests/{token}/confirm` | личный аккаунт | Подтвердить (204) |
| `POST /staff-link-requests/{token}/decline` | личный аккаунт | Отказаться (204) |
| `GET /admin/personal-unlink-requests?status=&page=&pageSize=` | админ, суперадмин | Запросы на снятие, новые сверху (`Pending`, `Approved`, `Rejected`). Админ видит запросы модераторов, суперадмин все |
| `POST /admin/personal-unlink-requests/{id}/approve` | админ, суперадмин | Одобрить: привязка снимается, тело `{ confirmation }` |
| `POST /admin/personal-unlink-requests/{id}/reject` | админ, суперадмин | Отклонить: тело `{ comment }` необязательно, подтверждение не нужно |
| `POST /admin/staff/{staffId}/personal-link/unlink` | админ, суперадмин | Снять привязку без запроса (например, при снятии модератора), тело `{ confirmation }` |
| `GET /admin/staff/{staffId}/personal-links` | админ, суперадмин | История привязок сотрудника: `personal` (`userId`, `name`, `email`), `linkedAt`, `unlinkedAt`, `unlinkedById`, `isActive` |

**Три экрана.** (1) Сотрудник (профиль): адрес личного аккаунта и подтверждение паролем и кодом; под ним состояние: «ждём подтверждения до …» с кнопкой «Отменить», либо «Привязан: имя, адрес» с кнопкой «Попросить снять» (поле причины) и статусом запроса, пока он ждёт решения. Кнопки «Снять» у сотрудника нет. (2) Владелец личного аккаунта: письмо ведёт на страницу `{Frontend:BaseUrl}/staff-link/{token}` (путь настраивается: `Frontend__PersonalLinkPath`); работает только для вошедшего обычного аккаунта (иначе предложите войти и вернуться по ссылке), показывает, кто просит и роль, и кнопки «Подтвердить» и «Отказаться». Токен запроса живёт 24 часа, не больше 5 запросов в час от сотрудника. (3) Админ: список запросов на снятие (причина, кто, какой личный аккаунт) с кнопками «Одобрить» (форма подтверждения паролем и кодом) и «Отклонить», а в карточке сотрудника история привязок.

**Ошибки.** Запрос на привязку: 400 подтверждения (раздел 1); 404 `personal_account_not_found` (нет такого действующего личного аккаунта с подтверждённой почтой: единый код для «нет», «служебный», «заблокирован»); 409 `staff_already_linked`, `personal_account_already_linked`; 429 `link_request_rate_limited`. Запрос на снятие: 400 `unlink_reason_too_short`/`_too_long`; 409 `not_linked`, `unlink_request_already_pending`. Решения админа: 403 `staff_role_not_allowed` (админов и суперадминов снимает только суперадмин), `cannot_change_own_rights` (себе и свой запрос нельзя); 404 `unlink_request_not_found`; 409 `invalid_state_transition` (запрос уже решён), `not_linked`; 400 подтверждения. Просмотр и подтверждение владельцем: 404 `link_request_not_found` (неизвестный токен или запрос адресован не вам); 403 `regular_account_required`; 409 `link_request_expired`, `link_request_cancelled`, `link_request_declined`, `link_request_already_confirmed`, `staff_already_linked`, `personal_account_already_linked`, `staff_not_active`.

**Уведомления** (только в интерфейсе): сотруднику `PersonalLinkConfirmed` (`userId`, `userName`, `conflictingCases`: сколько его обращений в работе теперь с конфликтом интересов, их стоит передать другому модератору), `PersonalLinkDeclined` (`userId`, `userName`) и `PersonalUnlinkDecided` (`requestId` (может быть пустым, если привязку сняли без запроса), `approved`); админам и суперадминам, которые вправе решить, `PersonalUnlinkRequested` (`requestId`, `staffId`, `staffName`).

## 4. Проверка локально (только Development)

Маршруты скрыты в OpenAPI и не существуют вне Development (404).

1. `POST /dev/seed-staff`: создаёт (один раз) модераторов `dev-moderator@example.com`, `dev-moderator-2@example.com` и админа `dev-admin@example.com`. **У всех один пароль `Dev-staff-pass-1`, 2FA включена с известным ключом.** В ответе на каждого: `accessToken`, `password`, `twoFactor.sharedKey` (положите в любое приложение-аутентификатор), `twoFactor.codes` (текущий и следующий код: подтверждение принимает код один раз, второе действие подтверждается вторым кодом, для третьего подождите или запросите заново), `twoFactor.secondsLeft`, `twoFactor.recoveryCodes` (резервные, показываются один раз, при включении 2FA).
2. Код без приложения-аутентификатора: `curl -X POST http://localhost:5023/dev/staff-code -H "Content-Type: application/json" -d '{"email":"dev-admin@example.com"}'` возвращает `codes` (текущий и следующий) и `secondsLeft`.
3. Приглашение: письмо уходит в почту, настроенную локально (Mailpit: http://localhost:8025 при `Smtp:Host=localhost`, `Smtp:Port=1025`, `Smtp:Security=None`), ссылка в письме открывает экран приёма.
4. Токен в заголовок: `Authorization: Bearer ...`.
