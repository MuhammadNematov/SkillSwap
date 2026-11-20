using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Reviews;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>
/// Работа модератора с обращениями (разделы 3, 8.2 и 10.2 ТЗ): очередь, взятие в работу, чтение чата обмена.
/// Доступ к чату возникает только при взятии обращения (<see cref="ChatAccessGrant"/> на именно этот обмен), даёт только чтение и каждое чтение попадает в аудит.
/// Админ и суперадмин в этом сервисе недоступны: служебная роль проверяется по базе, а не только по токену.
/// </summary>
public sealed class ModerationService
{
    public const int DefaultChatPageSize = 30;
    public const int MaxChatPageSize = 100;

    private readonly ICaseStore _cases;
    private readonly IReviewStore _reviews;
    private readonly IExchangeStore _exchanges;
    private readonly IMessageStore _messages;
    private readonly IChatAccessGrantStore _grants;
    private readonly ChatAccessPolicy _policy;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly IMessageCipher _cipher;
    private readonly TimeProvider _clock;
    private readonly ICaseMessageStore _caseMessages;

    public ModerationService(
        ICaseStore cases, IReviewStore reviews, IExchangeStore exchanges, IMessageStore messages, IChatAccessGrantStore grants, ChatAccessPolicy policy,
        IProfileStore profiles, IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify, IMessageCipher cipher, TimeProvider clock, ICaseMessageStore caseMessages)
    {
        _caseMessages = caseMessages;
        _cases = cases;
        _reviews = reviews;
        _exchanges = exchanges;
        _messages = messages;
        _grants = grants;
        _policy = policy;
        _profiles = profiles;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _cipher = cipher;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ---------- очередь и карточка ----------

    public async Task<ModerationCaseListDto> ListAsync(
        Guid moderatorId, ModeratorScope scope, CaseType? type, CasePriority? priority, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        var moderator = await RequireModeratorAsync(moderatorId, ct);
        var excluded = await ConflictUsersAsync(moderator, ct);
        var (items, total) = await _cases.ListForModeratorAsync(scope, moderatorId, excluded, type, priority, page, pageSize, ct);
        return new ModerationCaseListDto(await ToDtosAsync(moderatorId, items, ct), page, pageSize, total);
    }

    /// <summary>Обращение из очереди или взятое этим модератором. Чужое взятое и обращение с конфликтом интересов выглядят как несуществующие.</summary>
    public async Task<ModerationCaseDto> GetAsync(Guid moderatorId, Guid caseId, CancellationToken ct)
    {
        var moderator = await RequireModeratorAsync(moderatorId, ct);
        var found = await _cases.FindAsync(caseId, tracked: false, ct);
        var reviewer = found is not null && (await _cases.FindAppealAsync(caseId, tracked: false, ct))?.ReviewerModeratorId == moderatorId;
        if (found is null || (found.Status != CaseStatus.Open && found.ModeratorId != moderatorId && !reviewer) || HasConflict(await ConflictUsersAsync(moderator, ct), found))
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        return (await ToDtosAsync(moderatorId, [found], ct))[0];
    }

    // ---------- взятие в работу ----------

    /// <summary>
    /// Взять обращение (Open → InProgress). В одной транзакции под блокировкой обоих участников и модератора: проверка конфликта интересов, доступ к чату этого обмена,
    /// записи аудита и по одному уведомлению каждому из участников. Второй модератор, пришедший одновременно, получит 409.
    /// </summary>
    public async Task<ModerationCaseDto> TakeAsync(Guid moderatorId, Guid caseId, CancellationToken ct)
    {
        var moderator = await RequireModeratorAsync(moderatorId, ct);
        var peek = await _cases.FindAsync(caseId, tracked: false, ct) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
        var excluded = await ConflictUsersAsync(moderator, ct);
        if (HasConflict(excluded, peek))
        {
            throw new ForbiddenException("conflict_of_interest", "Вы не можете рассматривать это обращение: в нём участвует ваш личный аккаунт или человек, с которым вы вели обмен.");
        }

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([moderatorId, peek.OpenedById, peek.AgainstUserId], token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            var now = Now();
            found.Take(moderatorId);   // не Open: 409 invalid_state_transition

            _grants.Add(ChatAccessGrant.Create(found.Id, moderatorId, found.ExchangeId, GrantReason.Case, now));
            _audit.Add(new AuditEntry(moderatorId, AuditActions.CaseTaken, "Case", found.Id, new { exchangeId = found.ExchangeId, type = found.Type }));
            _audit.Add(new AuditEntry(moderatorId, AuditActions.ChatAccessGranted, "Exchange", found.ExchangeId, new { caseId = found.Id, moderatorId, reason = GrantReason.Case }));
            _notify.Add(found.OpenedById, NotificationType.CaseTaken, new { caseId = found.Id, exchangeId = found.ExchangeId, role = nameof(CaseRole.Opener) });
            _notify.Add(found.AgainstUserId, NotificationType.CaseTaken, new { caseId = found.Id, exchangeId = found.ExchangeId, role = nameof(CaseRole.Against) });
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        return await GetAsync(moderatorId, caseId, ct);
    }

    // ---------- чтение чата ----------

    /// <summary>
    /// История чата обмена по обращению для модератора. Нужен активный доступ именно к этому обмену, выданный по этому обращению. Каждый запрос пишется в аудит
    /// (кто, какое обращение, какой обмен, когда, адрес) до выдачи данных; участникам ничего не отправляется. Удалённые сообщения приходят с текстом,
    /// у исправленных есть прежние версии. Только чтение.
    /// </summary>
    public async Task<ModerationChatPageDto> ReadChatAsync(Guid moderatorId, Guid caseId, string? before, int? limit, CancellationToken ct)
    {
        var moderator = await RequireModeratorAsync(moderatorId, ct);
        var size = limit ?? DefaultChatPageSize;
        if (size < 1 || size > MaxChatPageSize)
        {
            throw new BadRequestException("invalid_limit", $"limit должен быть от 1 до {MaxChatPageSize}.");
        }

        var cursor = before is null ? ((DateTime, Guid)?)null : ChatService.DecodeCursor(before);
        var found = await _cases.FindAsync(caseId, tracked: false, ct) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
        var grant = await _grants.FindActiveAsync(moderatorId, found.ExchangeId, ct);
        if (grant is null || grant.CaseId != caseId || !await _policy.CanReadAsync(moderatorId, moderator.Role, found.ExchangeId, ct))
        {
            throw new ForbiddenException("chat_access_denied", "Нет доступа к чату этого обмена: он выдаётся при взятии обращения и закрывается при решении.");
        }

        var exchange = await _exchanges.GetViewAsync(found.ExchangeId, ct) ?? throw new NotFoundException("exchange_not_found", "Обмен не найден.");
        await _audit.WriteAsync(new AuditEntry(moderatorId, AuditActions.ChatRead, "Exchange", found.ExchangeId, new { caseId, limit = size, cursor = before }), ct);

        var page = await _messages.ListBeforeAsync(found.ExchangeId, cursor, size + 1, ct);
        var hasMore = page.Count > size;
        var items = page.Take(size).ToList();
        var nextBefore = hasMore ? ChatService.EncodeCursor(items[^1]) : null;
        items.Reverse();

        var ids = items.Select(m => m.Id).ToList();
        var revisions = await _messages.ListRevisionsAsync(ids, ct);
        var evidence = (await _cases.GetEvidenceIdsAsync([caseId], ct)).GetValueOrDefault(caseId)?.ToHashSet() ?? [];
        var attachments = await _messages.GetAttachmentsAsync(items.Where(m => m.AttachmentId is not null).Select(m => m.AttachmentId!.Value).ToList(), ct);

        var dtos = items.Select(m => new ModerationMessageDto(
            m.Id, m.SenderId, m.Type,
            m.EncryptedContent is null ? null : _cipher.Decrypt(m.EncryptedContent),   // удалённое сообщение хранится зашифрованным и для модератора читается
            m.PaymentWarning, m.AttachmentId,
            m.AttachmentId is { } a && attachments.TryGetValue(a, out var att) ? Files.AttachmentMapper.ToDto(att) : null,
            m.CreatedAt, m.EditedAt, m.DeletedAt, m.ReadAt, evidence.Contains(m.Id),
            revisions.TryGetValue(m.Id, out var list) ? list.Select(r => new MessageRevisionDto(_cipher.Decrypt(r.PreviousEncryptedContent), r.ChangedAt)).ToList() : [])).ToList();

        return new ModerationChatPageDto(caseId, found.ExchangeId, exchange.UserA, exchange.UserB, dtos, nextBefore);
    }

    // ---------- отзыв доступа ----------

    /// <summary>
    /// Отзывает все действующие доступы к чату по обращению (решение вынесено, апелляция закончена). Повтор безвреден. Вызывать внутри транзакции решения;
    /// изменения сохраняет вызывающий.
    /// </summary>
    public async Task<int> RevokeChatAccessAsync(Guid caseId, Guid? actorId, CancellationToken ct)
    {
        var now = Now();
        var revoked = 0;
        foreach (var grant in await _grants.ListActiveForCaseAsync(caseId, ct))
        {
            if (grant.Revoke(now))
            {
                revoked++;
                _audit.Add(new AuditEntry(actorId, AuditActions.ChatAccessRevoked, "Exchange", grant.ExchangeId, new { caseId, moderatorId = grant.ModeratorId }));
            }
        }

        return revoked;
    }

    // ---------- внутреннее ----------

    private async Task<User> RequireModeratorAsync(Guid moderatorId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(moderatorId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role != UserRole.Moderator || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("moderator_required", "Доступно только модератору.");
        }

        return user;
    }

    /// <summary>
    /// Кого модератор не может рассматривать (раздел 3 ТЗ, правило 3): его личный аккаунт и все, с кем этот личный аккаунт вёл обмен.
    /// Без привязанного личного аккаунта конфликта нет.
    /// </summary>
    private async Task<IReadOnlyCollection<Guid>> ConflictUsersAsync(User moderator, CancellationToken ct) =>
        await _cases.GetConflictUserIdsForStaffAsync(moderator.Id, ct);

    private static bool HasConflict(IReadOnlyCollection<Guid> conflictUsers, Case value) =>
        conflictUsers.Contains(value.OpenedById) || conflictUsers.Contains(value.AgainstUserId);

    private async Task<IReadOnlyList<ModerationCaseDto>> ToDtosAsync(Guid moderatorId, IReadOnlyList<Case> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var now = Now();
        var evidence = await _cases.GetEvidenceIdsAsync(items.Select(c => c.Id).ToList(), ct);
        var pending = await _caseMessages.CountUnansweredAsync(items.Select(c => c.Id).ToList(), null, ct);
        var appeals = await _cases.GetAppealsAsync(items.Select(c => c.Id).ToList(), ct);
        var exchanges = await _exchanges.GetViewsAsync(items.Select(c => c.ExchangeId).Distinct().ToList(), ct);
        var people = exchanges.Values.SelectMany(e => new[] { e.UserA, e.UserB }).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First() with { AvatarUrl = null });
        var reviews = new Dictionary<Guid, Review>();
        foreach (var reviewId in items.Where(c => c.ReviewId is not null).Select(c => c.ReviewId!.Value).Distinct())
        {
            if (await _reviews.FindAsync(reviewId, ct) is { } review)
            {
                reviews[reviewId] = review;
            }
        }

        var result = new List<ModerationCaseDto>();
        foreach (var c in items)
        {
            var mine = c.ModeratorId == moderatorId;
            var appeal = appeals.GetValueOrDefault(c.Id);
            var isReviewer = appeal?.ReviewerModeratorId == moderatorId;
            var access = (mine || isReviewer) && await _grants.FindActiveAsync(moderatorId, c.ExchangeId, ct) is { } g && g.CaseId == c.Id;
            var review = c.ReviewId is { } rid && reviews.TryGetValue(rid, out var r)
                ? new ModerationReviewDto(r.Id, r.AuthorId, r.TargetId, r.ScoreKnowledge, r.ScoreExplaining, r.ScorePunctuality, r.ScorePoliteness, r.Average, r.Text, r.Status)
                : null;
            result.Add(new ModerationCaseDto(
                c.Id, c.Type, c.Category, c.Status, c.Priority, c.ExchangeId, c.ReviewId, people[c.OpenedById], people[c.AgainstUserId], c.Description, c.FromCall,
                c.CreatedAt, c.DueAt, c.IsOverdue(now), c.ModeratorId, mine, access, evidence.GetValueOrDefault(c.Id) ?? [], review,
                c.Decision, c.AlsoWarned, c.DecisionReason, c.ResolvedAt, c.ResponseDeadlineAt, pending.GetValueOrDefault(c.Id),
                appeal is null ? null : new StaffAppealDto(
                    appeal.AppellantId, appeal.Reason, appeal.CreatedAt, appeal.AdminId, appeal.ReviewerModeratorId, appeal.ReviewerConclusion, appeal.Outcome, appeal.FinalDecision, appeal.DecidedAt),
                isReviewer));
        }

        return result;
    }
}
