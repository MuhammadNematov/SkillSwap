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

## 3. Проверка локально (только Development)

Маршруты скрыты в OpenAPI и не существуют вне Development (404).

1. `POST /dev/seed-staff`: создаёт (один раз) модераторов `dev-moderator@example.com`, `dev-moderator-2@example.com` и админа `dev-admin@example.com`. **У всех один пароль `Dev-staff-pass-1`, 2FA включена с известным ключом.** В ответе на каждого: `accessToken`, `password`, `twoFactor.sharedKey` (положите в любое приложение-аутентификатор), `twoFactor.codes` (текущий и следующий код: подтверждение принимает код один раз, второе действие подтверждается вторым кодом, для третьего подождите или запросите заново), `twoFactor.secondsLeft`, `twoFactor.recoveryCodes` (резервные, показываются один раз, при включении 2FA).
2. Код без приложения-аутентификатора: `curl -X POST http://localhost:5023/dev/staff-code -H "Content-Type: application/json" -d '{"email":"dev-admin@example.com"}'` возвращает `codes` (текущий и следующий) и `secondsLeft`.
3. Приглашение: письмо уходит в почту, настроенную локально (Mailpit: http://localhost:8025 при `Smtp:Host=localhost`, `Smtp:Port=1025`, `Smtp:Security=None`), ссылка в письме открывает экран приёма.
4. Токен в заголовок: `Authorization: Bearer ...`.
