using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>
/// Пояснения по обращению (раздел 8.2 ТЗ): модератор задаёт вопрос одному или обоим участникам (<c>InProgress → WaitingResponse</c>, на ответ 48 часов),
/// участники отвечают на свои вопросы. Когда ответили все или прошли 48 часов (задача <see cref="CaseJobs"/>), обращение возвращается в работу.
/// Всё меняется в одной транзакции под блокировкой модератора и обоих участников.
/// </summary>
public sealed class ExplanationService
{
    /// <summary>Сколько вопросов можно задать по одному обращению.</summary>
    public const int MaxQuestionsPerCase = 10;

    private readonly ICaseStore _cases;
    private readonly ICaseMessageStore _messages;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly IBackgroundJobs _jobs;
    private readonly TimeProvider _clock;

    public ExplanationService(
        ICaseStore cases, ICaseMessageStore messages, IProfileStore profiles, IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify,
        IBackgroundJobs jobs, TimeProvider clock)
    {
        _cases = cases;
        _messages = messages;
        _profiles = profiles;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _jobs = jobs;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ---------- модератор ----------

    /// <summary>
    /// Задать вопрос. Только модератор, взявший обращение (чужое и не взятое — 404). Обращение в работе: оно переходит в <c>WaitingResponse</c> и получает срок 48 часов;
    /// уже ждёт ответов — 409 <c>explanation_already_requested</c> (вопрос обоим задаётся одним запросом).
    /// </summary>
    public async Task<ExplanationThreadDto> AskAsync(Guid moderatorId, Guid caseId, AskQuestionRequest request, CancellationToken ct)
    {
        await RequireModeratorAsync(moderatorId, ct);
        var peek = await _cases.FindAsync(caseId, tracked: false, ct);
        if (peek is null || peek.ModeratorId != moderatorId)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        DateTime deadline = default;
        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([moderatorId, peek.OpenedById, peek.AgainstUserId], token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            if (found.Status == CaseStatus.WaitingResponse)
            {
                throw new ConflictException("explanation_already_requested", "Вы уже запросили пояснения: дождитесь ответов или истечения срока.");
            }

            var recipients = request.Recipient switch
            {
                ExplanationRecipient.Opener => new[] { found.OpenedById },
                ExplanationRecipient.Against => new[] { found.AgainstUserId },
                _ => new[] { found.OpenedById, found.AgainstUserId },
            };

            var asked = await _messages.CountQuestionsAsync(caseId, token);
            if (asked + recipients.Length > MaxQuestionsPerCase)
            {
                throw new ConflictException("too_many_questions", $"По одному обращению можно задать не больше {MaxQuestionsPerCase} вопросов.");
            }

            var now = Now();
            var questions = recipients.Select(r => CaseMessage.Create(caseId, moderatorId, r, request.Text)).ToList();   // пустой и длинный текст: 400
            found.RequestExplanation(moderatorId, now);   // не InProgress: 409 invalid_state_transition
            deadline = found.ResponseDeadlineAt!.Value;
            foreach (var question in questions)
            {
                _messages.Add(question);
                _notify.Add(question.RecipientId, NotificationType.CaseExplanationRequested,
                    new { caseId, exchangeId = found.ExchangeId, questionId = question.Id, deadlineAt = deadline });
            }

            _audit.Add(new AuditEntry(moderatorId, AuditActions.ExplanationRequested, "Case", caseId, new { recipients = request.Recipient, questions = questions.Count, deadlineAt = deadline }));
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        _jobs.Schedule<CaseJobs>(j => j.ExplanationDueAsync(caseId), new DateTimeOffset(deadline, TimeSpan.Zero));
        return await ThreadAsync(caseId, forUser: null, ct);
    }

    public async Task<ExplanationThreadDto> ListForModeratorAsync(Guid moderatorId, Guid caseId, CancellationToken ct)
    {
        await RequireModeratorAsync(moderatorId, ct);
        var found = await _cases.FindAsync(caseId, tracked: false, ct);
        if (found is null || found.ModeratorId != moderatorId)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        return await ThreadAsync(caseId, forUser: null, ct);
    }

    // ---------- участник ----------

    /// <summary>Ответить на вопрос модератора. Пока ждём ответов и срок не вышел; на каждый вопрос один ответ. Ответили на все вопросы — обращение возвращается в работу.</summary>
    public async Task<ExplanationThreadDto> AnswerAsync(Guid userId, Guid caseId, AnswerQuestionRequest request, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var peek = await _cases.FindAsync(caseId, tracked: false, ct);
        if (peek is null || !peek.IsParty(userId) || peek.ModeratorId is null)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([peek.ModeratorId.Value, peek.OpenedById, peek.AgainstUserId], token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            var now = Now();
            if (found.Status != CaseStatus.WaitingResponse)
            {
                throw new ConflictException("explanation_not_requested", "Модератор сейчас не ждёт пояснений по этому обращению.");
            }

            if (now > found.ResponseDeadlineAt!.Value)
            {
                throw new ConflictException("explanation_window_closed", "Срок ответа (48 часов) истёк.");
            }

            var question = request.QuestionId == Guid.Empty ? null : await _messages.FindAsync(request.QuestionId, token);
            if (question is null || question.CaseId != caseId || question.InReplyToId is not null || question.RecipientId != userId)
            {
                throw new NotFoundException("question_not_found", "Вопрос не найден.");   // чужой вопрос выглядит как несуществующий
            }

            if ((await _messages.AnsweredQuestionIdsAsync([question.Id], token)).Contains(question.Id))
            {
                throw new ConflictException("question_already_answered", "На этот вопрос вы уже ответили.");
            }

            _messages.Add(CaseMessage.Create(caseId, userId, question.SenderId, request.Text, question.Id));   // пустой и длинный текст: 400
            await _messages.SaveAsync(token);
            _audit.Add(new AuditEntry(userId, AuditActions.ExplanationAnswered, "Case", caseId, new { questionId = question.Id }));
            if (!await _messages.HasUnansweredAsync(caseId, token))
            {
                found.ResumeAfterExplanation();   // ответили все: обращение снова в работе
            }

            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        return await ThreadAsync(caseId, forUser: userId, ct);
    }

    /// <summary>Моя переписка по обращению: вопросы мне и мои ответы. Обвиняемый видит её, когда обращение взято (до этого его в обращении нет).</summary>
    public async Task<ExplanationThreadDto> ListForParticipantAsync(Guid userId, Guid caseId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var found = await _cases.FindAsync(caseId, tracked: false, ct);
        if (found is null || !found.IsParty(userId) || found.Status is CaseStatus.Open or CaseStatus.Withdrawn)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        return await ThreadAsync(caseId, forUser: userId, ct);
    }

    // ---------- внутреннее ----------

    private async Task<ExplanationThreadDto> ThreadAsync(Guid caseId, Guid? forUser, CancellationToken ct)
    {
        var found = await _cases.FindAsync(caseId, tracked: false, ct) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
        var items = await _messages.ListAsync(caseId, forUser, ct);
        var answered = await _messages.AnsweredQuestionIdsAsync(items.Where(m => m.InReplyToId is null).Select(m => m.Id).ToList(), ct);
        var dtos = items.Select(m => new ExplanationDto(
            m.Id, m.InReplyToId is null ? ExplanationKind.Question : ExplanationKind.Answer, m.SenderId, m.RecipientId, m.Text, m.InReplyToId,
            m.InReplyToId is null && answered.Contains(m.Id), m.CreatedAt)).ToList();
        return new ExplanationThreadDto(caseId, found.Status, found.Status == CaseStatus.WaitingResponse ? found.ResponseDeadlineAt : null, dtos);
    }

    private async Task RequireModeratorAsync(Guid moderatorId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(moderatorId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role != UserRole.Moderator || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("moderator_required", "Доступно только модератору.");
        }
    }

    private async Task RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Доступно только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }
}

/// <summary>Фоновые задачи по обращениям. Идемпотентны и сверяются с состоянием, а не с тем, что было при постановке.</summary>
public sealed class CaseJobs
{
    /// <summary>Задача может сработать чуть раньше срока (часы планировщика и приложения расходятся): такой запас допускается.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

    private readonly ICaseStore _cases;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;

    public CaseJobs(ICaseStore cases, IUnitOfWork uow, IAuditLog audit, TimeProvider clock)
    {
        _cases = cases;
        _uow = uow;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>Прошли 48 часов на ответ: обращение возвращается в работу (WaitingResponse → InProgress) с тем, что есть. Уже вернувшееся или ещё не созревшее не трогается.</summary>
    public async Task ExplanationDueAsync(Guid caseId)
    {
        var peek = await _cases.FindAsync(caseId, tracked: false, CancellationToken.None);
        if (peek is null || peek.Status != CaseStatus.WaitingResponse || peek.ModeratorId is null)
        {
            return;
        }

        await _uow.ExecuteAsync(async ct =>
        {
            await _uow.LockUsersAsync([peek.ModeratorId.Value, peek.OpenedById, peek.AgainstUserId], ct);
            var found = await _cases.FindAsync(caseId, tracked: true, ct);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (found is null || found.Status != CaseStatus.WaitingResponse || now < found.ResponseDeadlineAt!.Value - Tolerance)
            {
                return 0;
            }

            if (found.ResumeAfterExplanation())
            {
                _audit.Add(new AuditEntry(null, AuditActions.ExplanationExpired, "Case", caseId));
                await _cases.SaveAsync(ct);
            }

            return 0;
        }, CancellationToken.None);
    }
}
