using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>
/// Подача обращения (раздел 8.2 ТЗ).
/// </summary>
/// <param name="Type">Спор по отзыву, жалоба на поведение, жалоба на неявки и срыв.</param>
/// <param name="Category">Категория, допустимая для типа (<see cref="CaseRules.CategoriesFor"/>).</param>
/// <param name="ExchangeId">Обмен, по которому подаётся обращение.</param>
/// <param name="ReviewId">Только для спора по отзыву: оспариваемый отзыв (о заявителе, опубликованный).</param>
/// <param name="Description">Описание, 20–2000 символов.</param>
/// <param name="MessageIds">Сообщения чата этого обмена как доказательства (не больше 20; удалённые можно).</param>
/// <param name="FromCall">Жалоба из видеозвонка (только на поведение): обмен замораживается сразу, нужно быть в комнате.</param>
public sealed record OpenCaseRequest(
    CaseType Type, CaseCategory Category, Guid ExchangeId, Guid? ReviewId, string? Description, IReadOnlyList<Guid>? MessageIds, bool FromCall = false);

/// <summary>Чьими глазами показано обращение.</summary>
public enum CaseRole
{
    /// <summary>Я подал обращение.</summary>
    Opener,

    /// <summary>Обращение подано на меня.</summary>
    Against,
}

/// <summary>Какие обращения перечислять в <c>GET /me/cases</c>.</summary>
public enum CaseListFilter
{
    All,
    Opened,
    Against,
}

/// <summary>Обращение для его участника.</summary>
/// <param name="MyRole">Кем я в этом обращении являюсь.</param>
/// <param name="EvidenceMessageIds">Сообщения, приложенные как доказательства.</param>
/// <param name="Decision">Решение: появляется, когда обращение решено.</param>
/// <param name="ResponseDeadlineAt">Пока ждём пояснений: до какого времени отвечать.</param>
/// <param name="CanWithdraw">Заявитель может отозвать обращение (пока его не взяли в работу).</param>
/// <param name="PendingQuestions">Сколько вопросов модератора ждут моего ответа (ответить: <c>POST /cases/{id}/explanations</c>).</param>
/// <param name="CanAppeal">Я могу подать апелляцию: обращение решено, не прошло 7 дней, апелляции ещё не было (<c>POST /cases/{id}/appeal</c>).</param>
/// <param name="Appeal">Апелляция по обращению, если она подана.</param>
public sealed record CaseDto(
    Guid Id,
    CaseType Type,
    CaseCategory Category,
    CaseStatus Status,
    CasePriority Priority,
    Guid ExchangeId,
    Guid? ReviewId,
    RequestUserDto OpenedBy,
    RequestUserDto Against,
    CaseRole MyRole,
    string Description,
    bool FromCall,
    DateTime CreatedAt,
    IReadOnlyList<Guid> EvidenceMessageIds,
    CaseDecision? Decision,
    bool AlsoWarned,
    string? DecisionReason,
    DateTime? ResolvedAt,
    DateTime? ResponseDeadlineAt,
    bool CanWithdraw,
    int PendingQuestions,
    bool CanAppeal,
    CaseAppealDto? Appeal);

public sealed record CaseListDto(IReadOnlyList<CaseDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Хранилище обращений (таблицы Cases и CaseEvidence).</summary>
public interface ICaseStore
{
    void Add(Case value);

    void AddEvidence(CaseEvidence evidence);

    /// <summary>Обращение для изменения (с отслеживанием). Менять только под блокировкой обоих участников.</summary>
    Task<Case?> FindAsync(Guid id, bool tracked, CancellationToken ct);

    /// <summary>Сколько обращений заявителя ещё не решено окончательно (не отозвано и не закрыто).</summary>
    Task<int> CountActiveByOpenerAsync(Guid openerId, CancellationToken ct);

    /// <summary>Сколько обращений заявитель подал с указанного времени (отозванные тоже считаются: иначе лимит обходится).</summary>
    Task<int> CountOpenedSinceAsync(Guid openerId, DateTime since, CancellationToken ct);

    /// <summary>Есть ли у заявителя активное обращение такого типа по этому обмену.</summary>
    Task<bool> HasActiveAsync(Guid openerId, Guid exchangeId, CaseType type, CancellationToken ct);

    /// <summary>Есть ли по отзыву спор, который не был отозван (в том числе уже решённый: оспорить дважды нельзя).</summary>
    Task<bool> HasDisputeForReviewAsync(Guid reviewId, CancellationToken ct);

    /// <summary>Есть ли по обмену другое активное обращение, поданное из звонка (оно держит обмен замороженным).</summary>
    Task<bool> HasActiveFromCallAsync(Guid exchangeId, Guid? exceptCaseId, CancellationToken ct);

    /// <summary>
    /// Обращения участника: поданные им и поданные на него; последние видны только когда обращение взято в работу (до этого его видит один заявитель).
    /// </summary>
    Task<(IReadOnlyList<Case> Items, int Total)> ListForUserAsync(Guid userId, CaseListFilter filter, int page, int pageSize, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetEvidenceIdsAsync(IReadOnlyCollection<Guid> caseIds, CancellationToken ct);

    /// <summary>
    /// Обращения для модератора. <see cref="ModeratorScope.Queue"/>: не взятые, сначала высокий приоритет, затем ближайший срок; <see cref="ModeratorScope.Mine"/>: взятые им и не закрытые.
    /// Обращения, где участвует кто-то из <paramref name="excludedUsers"/> (конфликт интересов), не возвращаются.
    /// </summary>
    Task<(IReadOnlyList<Case> Items, int Total)> ListForModeratorAsync(
        ModeratorScope scope, Guid moderatorId, IReadOnlyCollection<Guid> excludedUsers, CaseType? type, CasePriority? priority, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Все, с кем у сотрудника конфликт интересов (раздел 3 ТЗ, правило 3): **все** личные аккаунты, когда-либо привязанные к нему (история привязок, а также текущая привязка),
    /// и все, с кем эти личные аккаунты когда-либо вели обмен. Снятие привязки конфликт не снимает.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetConflictUserIdsForStaffAsync(Guid staffId, CancellationToken ct);

    void AddAppeal(CaseAppeal appeal);

    Task<CaseAppeal?> FindAppealAsync(Guid caseId, bool tracked, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, CaseAppeal>> GetAppealsAsync(IReadOnlyCollection<Guid> caseIds, CancellationToken ct);

    /// <summary>Действующие админы и суперадмины (получатели уведомления об апелляции).</summary>
    Task<IReadOnlyList<Guid>> ListActiveAdminIdsAsync(CancellationToken ct);

    /// <summary>Действующие модераторы (для выбора проверяющего по апелляции).</summary>
    Task<IReadOnlyList<User>> ListModeratorsAsync(CancellationToken ct);

    /// <summary>Все обращения для админа: фильтры по статусу и типу, новые сверху.</summary>
    Task<(IReadOnlyList<Case> Items, int Total)> ListForAdminAsync(CaseStatus? status, CaseType? type, int page, int pageSize, CancellationToken ct);

    /// <summary>Предупреждения, вынесенные по обращению (с отслеживанием): их отзывают при отмене решения.</summary>
    Task<IReadOnlyList<Warning>> FindWarningsForCaseAsync(Guid caseId, CancellationToken ct);

    Task<IReadOnlyList<Warning>> ListWarningsForCaseReadOnlyAsync(Guid caseId, CancellationToken ct);

    Task<BanRequest?> FindBanRequestAsync(Guid id, CancellationToken ct);

    /// <summary>Запросы на бан, созданные по обращению (с отслеживанием).</summary>
    Task<IReadOnlyList<BanRequest>> FindBanRequestsForCaseAsync(Guid caseId, CancellationToken ct);

    /// <summary>
    /// Обращения, которые держит модератор: взятые им (InProgress, WaitingResponse) и те, где он назначен проверяющим по апелляции (AppealReview). С <paramref name="conflictUsers"/>
    /// только те, где участвует кто-то из этих пользователей.
    /// </summary>
    Task<IReadOnlyList<Case>> FindHeldByModeratorAsync(Guid moderatorId, IReadOnlyCollection<Guid>? conflictUsers, bool tracked, CancellationToken ct);

    /// <summary>Апелляции без окончательного решения, которые ведёт этот админ (с отслеживанием).</summary>
    Task<IReadOnlyList<CaseAppeal>> FindUndecidedAppealsOfAdminAsync(Guid adminId, CancellationToken ct);

    /// <summary>Необработанные запросы на бан по пользователю (с отслеживанием).</summary>
    Task<IReadOnlyList<BanRequest>> FindPendingBanRequestsForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>Все запросы на бан по пользователю, новые сверху (только чтение).</summary>
    Task<IReadOnlyList<BanRequest>> ListBanRequestsForUserAsync(Guid userId, CancellationToken ct);

    Task<(IReadOnlyList<BanRequest> Items, int Total)> ListBanRequestsAsync(BanRequestStatus? status, int page, int pageSize, CancellationToken ct);

    void AddWarning(Warning warning);

    void AddBanRequest(BanRequest request);

    /// <summary>Сколько у пользователя действующих предупреждений (не отозваны, не истекли).</summary>
    Task<int> CountActiveWarningsAsync(Guid userId, DateTime now, CancellationToken ct);

    /// <summary>Есть ли по пользователю необработанный запрос на бан.</summary>
    Task<bool> HasPendingBanRequestAsync(Guid userId, CancellationToken ct);

    /// <summary>Есть ли по обмену другая жалоба (поведение, неявки и срыв), которая ещё не закрыта окончательно: она может требовать заморозки.</summary>
    Task<bool> HasOtherActiveComplaintAsync(Guid exchangeId, Guid exceptCaseId, CancellationToken ct);

    /// <summary>Есть ли у пользователя обращение (как у заявителя или как у того, на кого подано), которое ещё не закрыто окончательно и не отозвано.</summary>
    Task<bool> HasActiveCaseAsync(Guid userId, CancellationToken ct);

    /// <summary>Сколько обращений модератора сейчас в работе (взятые им и повторные проверки по апелляции), где участвует кто-то из <paramref name="conflictUsers"/>.</summary>
    Task<int> CountActiveConflictsAsync(Guid moderatorId, IReadOnlyCollection<Guid> conflictUsers, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}
