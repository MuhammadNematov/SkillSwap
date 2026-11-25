# Отзывы, обращения и модерация: контракт для фронтенда (этап 6)

Единая сводка для фронтенда по разделам 8.1–8.3, 9.4 и 10.2 ТЗ. Точные схемы запросов и ответов лежат в `docs/openapi.json` (тег **Reviews**, **Cases**, **Moderation**, **Admin**), подробности по шагам: `docs/BACKEND-DEV.md`. Здесь: кто что видит, какие статусы и уведомления бывают, какие коды ошибок обрабатывать и как проверить всё локально.

Ошибки: `ProblemDetails` с полем `code` (ниже перечислены коды, которые стоит показывать пользователю по-разному). Все перечисления приходят строками.

## 1. Кто что делает

| Роль | Экраны | Маршруты |
| --- | --- | --- |
| Обычный пользователь | Отзыв по обмену, отзывы в профиле, подача обращения, «Мои обращения», карточка обращения, ответы на вопросы модератора, апелляция | `Reviews`, `Cases` (маршруты `/me/cases`, `/cases/...`, `/exchanges/{id}/reviews`, `/users/{id}/reviews`) |
| Модератор | Очередь, мои обращения, карточка, чат обмена по обращению, вопросы сторонам, решение, повторные проверки по апелляции | `/moderation/...` |
| Админ и суперадмин | Все обращения и материалы, апелляции, выбор проверяющего, окончательное решение, запросы на блокировку | `/admin/...` |

Служебным аккаунтам закрыты обычные маршруты (403 `regular_account_required`), обычным пользователям закрыты `/moderation` и `/admin` (403). **Админ никогда не читает чат**: в его материалах только сообщения, приложенные к обращению как доказательства.

## 2. Отзывы (раздел 8.1)

| Метод | Что делает |
| --- | --- |
| `GET /exchanges/{id}/reviews` | Что я могу: `canWrite`, `unavailableReason`, `deadlineAt`, `isFullExchange`, `oneSided`, `canEdit`, `myReview`, `reviewAboutMe` |
| `POST /exchanges/{id}/reviews` | Оставить отзыв (4 оценки 1–5 и текст 20–1000) |
| `PUT /exchanges/{id}/reviews` | Править, пока не опубликован |
| `GET /users/{id}/reviews` | Опубликованные отзывы о человеке и рейтинг (`ratingAvg` = `null` и `isNewcomer`, пока меньше 3) |

Отзывы слепые: опубликуются вместе, когда написали оба, либо через 7 дней после первого. Право на отзыв: обмен закрыт, была хотя бы одна засчитанная сессия (иначе только пострадавший от трёх неявок, `oneSided`), срок 14 дней. Коды `unavailableReason`: `exchange_not_closed`, `partner_deleted`, `no_counted_sessions`, `review_window_closed`, `review_already_exists`. Статус отзыва: `Pending`, `Published`, `Hidden` (скрыт модератором), `Frozen` (оспаривается: адресату и в профиле показывается как опубликованный, в рейтинг не входит).

## 3. Подача обращения (раздел 8.2)

`POST /cases` (201 и `CaseDto`): `type`, `category`, `exchangeId`, `reviewId` (только спор), `description` (20–2000), `messageIds` (до 20 сообщений этого обмена), `fromCall`.

| `type` | `category` | Срок подачи | Приоритет |
| --- | --- | --- | --- |
| `ReviewDispute` | `ReviewUnfair`, `ReviewAbusive`, `ReviewFalse` | 14 дней после публикации отзыва | обычный |
| `Conduct` | `Insults`, `Threats`, `Harassment`, `Spam`, `Extortion` | 90 дней после закрытия обмена | высокий |
| `NoShowAbuse` | `SystematicEvasion`, `BadFaithLessons` | 30 дней после закрытия обмена | обычный |

Высокий приоритет также при `fromCall` и когда одному из двоих меньше 18. Жалоба из звонка (`fromCall: true`, только `Conduct`) замораживает обмен сразу, нужно быть в комнате идущего урока. Спор по отзыву замораживает отзыв. Обвиняемый **ничего не знает об обращении, пока модератор не взял его в работу** (`GET /cases/{id}` до этого 404 `case_not_found`).

Коды при подаче: 400 `case_description_too_short`/`_too_long`, `case_category_mismatch`, `case_review_required`, `case_review_not_allowed`, `case_from_call_conduct_only`, `too_many_evidence`, `evidence_not_in_exchange`; 404 `exchange_not_found`, `review_not_found`; 409 `partner_deleted`, `filing_window_closed`, `case_already_open`, `case_already_exists`, `review_already_disputed`, `too_many_open_cases` (больше 3), `not_in_call`; 429 `case_rate_limited` (больше 5 за сутки).

Участнику: `GET /me/cases?role=All|Opened|Against`, `GET /cases/{id}`, `POST /cases/{id}/withdraw` (пока `canWithdraw`), `GET /cases/{id}/explanations`, `POST /cases/{id}/explanations`, `POST /cases/{id}/appeal`.

Поля `CaseDto`, которые управляют экраном: `status`, `myRole` (`Opener`, `Against`), `canWithdraw`, `pendingQuestions` и `responseDeadlineAt` (ждут моего ответа), `decision`, `alsoWarned`, `decisionReason`, `resolvedAt` (после решения), `canAppeal`, `appeal` (`reviewInProgress`, `outcome`, `finalDecision`).

## 4. Статусы обращения

| Статус | Значение | Что видит и может участник |
| --- | --- | --- |
| `Open` | Подано, ждёт модератора | Заявитель: может отозвать. Обвиняемый: не видит |
| `Withdrawn` | Отозвано заявителем | Конец |
| `InProgress` | Взято модератором | Читает чат модератор (участникам пришло `CaseTaken`) |
| `WaitingResponse` | Ждём пояснений (срок `responseDeadlineAt`) | Отвечает на свои вопросы |
| `Resolved` | Решено | Видит решение и обоснование, может подать апелляцию (`canAppeal`) |
| `Appealed` | Подана апелляция, ждёт админа | Ждёт |
| `AppealReview` | Повторную проверку чата ведёт другой модератор | Ждёт (`CaseAppealReviewAssigned`) |
| `Closed` | Закрыто: 7 дней без апелляции или решение админа | Конец |

Переходы: `Open → InProgress → WaitingResponse → InProgress → Resolved → Closed`, а также `Resolved → Appealed → (AppealReview → Appealed →) Closed`. `Open → Withdrawn` только заявителем.

## 5. Решения

| Тип | `decision` | Последствия |
| --- | --- | --- |
| Спор | `ReviewKept` | Отзыв возвращается в рейтинг |
| Спор | `ReviewHidden` | Отзыв скрыт, рейтинг пересчитан |
| Жалобы | `Warning` | Предупреждение на 12 месяцев; обмен, замороженный жалобой, размораживается |
| Жалобы | `ExchangeEnded` | Обмен завершён принудительно (причина `moderation`), у обоих право на отзыв |
| Жалобы | `FrozenBanRequested` | Обмен заморожен, запрос на блокировку админу |
| Жалобы | `NoViolation` | Нарушения нет, обмен размораживается |

`alsoWarned` добавляет предупреждение (к `ReviewHidden`, `ExchangeEnded`, `FrozenBanRequested`). Три действующих предупреждения за 12 месяцев создают запрос на блокировку. Решение всегда с обоснованием (20–2000), его видят обе стороны. Решение админа по апелляции (`outcome`): `Upheld`, `Overturned`, `Changed` (отмена и замена откатывают отзыв, предупреждение, заморозку; завершение обмена и одобренную блокировку откатить нельзя).

## 6. Модератор и админ

Модератор (`/moderation`): `GET cases?scope=Queue|Mine|Appeals&type=&priority=` (очередь: сначала высокий приоритет, затем срок `dueAt`, `isOverdue`), `GET cases/{id}`, `POST cases/{id}/take`, `GET cases/{id}/chat?before=&limit=` (каждый запрос пишется в аудит; удалённые сообщения с текстом, у правленых `revisions`, доказательства `isEvidence`), `POST cases/{id}/questions`, `GET cases/{id}/explanations`, `POST cases/{id}/stop-waiting` (с причиной), `POST cases/{id}/resolve`, `POST cases/{id}/freeze-exchange`, `POST cases/{id}/unfreeze-exchange`, `POST cases/{id}/appeal-conclusion`. Конфликт интересов: обращение не видно в очереди, `take` даёт 403 `conflict_of_interest`. Чат закрывается решением или заключением: дальше 403 `chat_access_denied`.

Админ (`/admin`): `GET cases?status=&type=&q=`, `GET cases/{id}` (материалы), `GET moderators`, `POST cases/{id}/appeal/reviewer`, `POST cases/{id}/appeal/decision`, `GET ban-requests?status=&q=`, `POST ban-requests/{id}/approve|reject`. Одобрение блокировки: аккаунт сразу `Blocked`, сеансы отозваны, соединения SignalR оборваны, обмены прерваны (партнёру нейтральная причина `partner_unavailable`), ожидающие заявки истекают, заблокированному уходит письмо.

Коды модерации и админа: 403 `moderator_required`, `admin_required`, `conflict_of_interest`, `chat_access_denied`; 404 `case_not_found`, `question_not_found`, `moderator_not_found`, `ban_request_not_found`; 409 `invalid_state_transition`, `waiting_for_explanations`, `explanation_already_requested`, `too_many_questions`, `explanation_not_requested`, `explanation_window_closed`, `question_already_answered`, `exchange_not_open`, `exchange_not_active`, `exchange_not_frozen`, `case_type_not_supported`, `case_not_in_work`, `appeal_review_in_progress`, `nothing_to_overturn`, `not_a_moderator`; 400 `case_decision_not_allowed`, `case_warning_not_combinable`, `case_reason_too_short`, `appeal_reason_too_short`, `appeal_window_closed`, `appeal_conclusion_too_short`, `appeal_reviewer_must_differ`, `appeal_final_decision_mismatch`, `stop_waiting_reason_too_short`, `user_not_active`.

## 7. Уведомления

Приходят в `GET /notifications` и событием `notification` в хабе `/hubs/notifications`; данные в `payload`. «Письмо»: дублируется письмом на языке пользователя (tg, ru, en).

| Тип | Кому | Данные (`payload`) | Письмо |
| --- | --- | --- | --- |
| `ReviewAvailable` | участнику | `exchangeId`, `userId`, `userName`, `deadlineAt` | нет |
| `ReviewReminder` | участнику | как выше | нет |
| `ReviewPublished` | адресату отзыва | `exchangeId`, `reviewId`, `userId`, `userName` | нет |
| `ExchangeFrozen` | обоим | `exchangeId` | нет |
| `ExchangeUnfrozen` | обоим | `exchangeId` | нет |
| `CaseTaken` | обоим | `caseId`, `exchangeId`, `role` | нет |
| `CaseExplanationRequested` | получателю вопроса | `caseId`, `exchangeId`, `questionId`, `deadlineAt` | нет |
| `CaseExplanationReminder` | не ответившему (за 6 часов до срока) | как выше | нет |
| `CaseReturnedToWork` | модератору | `caseId`, `reason` (`AllAnswered`, `Expired`) | нет |
| `CaseResolved` | обоим | `caseId`, `exchangeId`, `role`, `decision`, `alsoWarned`, `youWereWarned`, `appealUntil` | да |
| `WarningIssued` | виновному | `caseId`, `exchangeId`, `reason` | нет |
| `WarningRevoked` | виновному | `caseId` | нет |
| `CaseAppealed` | второй стороне и админам | `caseId`, `exchangeId` | нет |
| `CaseAppealReviewAssigned` | обоим | `caseId`, `exchangeId`, `role` | нет |
| `AppealReviewAssigned` | проверяющему | `caseId`, `exchangeId` | нет |
| `AppealConclusionReady` | админу | `caseId` | нет |
| `AppealDecided` | обоим | `caseId`, `exchangeId`, `role`, `outcome`, `decision`, `alsoWarned`, `youWereWarned`, `warningRevoked` | да |
| `BanRequestDecided` | запросившему модератору | `banRequestId`, `userId`, `approved` | нет |
| `CaseOverdue` | админам | `caseId`, `priority`, `dueAt` | нет |

Одно уведомление на событие: чтение чата модератором уведомлений не создаёт. Перед показом карточки по `caseId` запросите обращение заново (уведомление не содержит текста решения).

## 8. Сроки

| Что | По ТЗ | Сокращённые сроки в разработке (`Cases:*`) |
| --- | --- | --- |
| Срок реакции на высокий приоритет | 24 часа | 3 минуты |
| Срок реакции на обычный | 72 часа | 5 минут |
| Ответ на вопрос модератора | 48 часов | 4 минуты |
| Напоминание перед концом срока ответа | за 6 часов | за 2 минуты |
| Апелляция и закрытие без неё | 7 дней | 6 минут |

Сроки подачи (14, 30 и 90 дней), слепая публикация отзыва и очистка чатов (90 дней) не сокращаются. Просроченный срок реакции (`isOverdue`) отмечается часовой сверкой.

## 9. Проверка локально (только Development)

Маршруты скрыты в OpenAPI и не существуют вне Development (404). Backend: `dotnet run --project src/SkillSwap.Api` (Development), нужен стартовый каталог навыков.

1. `POST /dev/seed-staff`: создаёт один раз модератора `dev-moderator@example.com`, второго модератора и админа `dev-admin@example.com`; в ответе `accessToken` каждого (служебным вход требует 2FA, поэтому для разработки выдаются токены; пароль виден только при создании). Токены живут недолго, маршрут можно вызывать снова.
2. `POST /dev/seed-case` с телом `{ "type": "Conduct", "stage": "Open", "decision": "Warning" }` (все поля необязательны):
   - `type`: `Conduct`, `NoShowAbuse`, `ReviewDispute`;
   - `stage`: `Open` (очередь), `Taken`, `Waiting` (вопрос задан), `Resolved`, `Appealed`, `AppealReview`;
   - `decision`: решение для стадий от `Resolved` (по умолчанию `Warning`, для спора `ReviewHidden`).
   Создаёт двух новых пользователей (заявитель и ответчик, вход по почте и паролю из ответа, токены тоже есть), обмен с перепиской (последние два сообщения приложены как доказательства) и обращение, доведённое до стадии **настоящими сервисами**, поэтому появляются настоящие уведомления и письма. В ответе: `caseId`, пользователи с токенами, модераторы, админ, действующие сроки (`timers`) и `caseUrl`.
3. Токен вставляется в заголовок `Authorization: Bearer ...`; реальный вход обычных пользователей по `POST /auth/login`.

Сроки в разработке сокращены в `appsettings.Development.json` (секция `Cases`); вне Development другие значения не принимаются, приложение не запустится.
