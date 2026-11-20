using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>Досрочное завершение ожидания пояснений: <c>reason</c> 20–2000 символов, пишется в аудит.</summary>
public sealed record StopWaitingRequest(string? Reason);

/// <summary>Апелляция на решение: <c>reason</c> 20–2000 символов.</summary>
public sealed record FileAppealRequest(string? Reason);

/// <summary>Админ назначает другого модератора на повторную проверку чата.</summary>
public sealed record AssignReviewerRequest(Guid ModeratorId);

/// <summary>Заключение модератора по апелляции: 20–4000 символов.</summary>
public sealed record AppealConclusionRequest(string? Conclusion);

/// <summary>
/// Окончательное решение админа. <c>Upheld</c> — решение остаётся; <c>Overturned</c> — решение отменено (для спора возвращается <c>ReviewKept</c>, для жалоб <c>NoViolation</c>), нужен <c>reason</c>;
/// <c>Changed</c> — заменено на <c>newDecision</c> (с <c>alsoWarned</c>), нужен <c>reason</c>. Для <c>Upheld</c> <c>reason</c> необязателен.
/// </summary>
public sealed record DecideAppealRequest(AppealOutcome Outcome, CaseDecision? NewDecision, bool AlsoWarned, string? Reason);

/// <summary>Решение по запросу на блокировку: комментарий необязателен, пишется в аудит.</summary>
public sealed record DecideBanRequest(string? Comment);

/// <summary>Апелляция глазами участника: без заключения проверяющего модератора.</summary>
/// <param name="ReviewInProgress">Повторную проверку проводит другой модератор.</param>
public sealed record CaseAppealDto(
    Guid AppellantId, string Reason, DateTime FiledAt, bool ReviewInProgress, AppealOutcome? Outcome, CaseDecision? FinalDecision, DateTime? DecidedAt);

/// <summary>Апелляция глазами модератора и админа: с заключением проверяющего.</summary>
public sealed record StaffAppealDto(
    Guid AppellantId, string Reason, DateTime FiledAt, Guid? AdminId, Guid? ReviewerModeratorId, string? ReviewerConclusion,
    AppealOutcome? Outcome, CaseDecision? FinalDecision, DateTime? DecidedAt);

/// <summary>Обращение в списке и карточке админа: статус, решение, апелляция. Переписка обмена админу недоступна.</summary>
public sealed record AdminCaseDto(
    Guid Id,
    CaseType Type,
    CaseCategory Category,
    CaseStatus Status,
    CasePriority Priority,
    Guid ExchangeId,
    Guid? ReviewId,
    RequestUserDto OpenedBy,
    RequestUserDto Against,
    string Description,
    bool FromCall,
    DateTime CreatedAt,
    Guid? ModeratorId,
    CaseDecision? Decision,
    bool AlsoWarned,
    string? DecisionReason,
    DateTime? ResolvedAt,
    DateTime? ClosedAt,
    StaffAppealDto? Appeal);

public sealed record AdminCaseListDto(IReadOnlyList<AdminCaseDto> Items, int Page, int PageSize, int TotalCount);

public sealed record WarningDto(Guid Id, Guid UserId, Guid IssuedById, string Reason, DateTime IssuedAt, DateTime ExpiresAt, DateTime? RevokedAt);

public sealed record BanRequestDto(
    Guid Id, Guid UserId, string UserName, Guid? CaseId, Guid? RequestedById, string Reason, BanRequestStatus Status, Guid? DecidedById, DateTime? DecidedAt, DateTime CreatedAt);

public sealed record BanRequestListDto(IReadOnlyList<BanRequestDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Модератор для выбора проверяющего по апелляции.</summary>
public sealed record ModeratorOptionDto(Guid Id, string Name);

/// <summary>
/// Материалы обращения для админа (раздел 3 ТЗ): всё, кроме полной переписки. Из чата доступны только сообщения, приложенные как доказательства
/// (с прежними версиями и удалённые). Каждый просмотр материалов записывается в аудит.
/// </summary>
public sealed record AdminCaseDetailDto(
    AdminCaseDto Case,
    ModerationReviewDto? Review,
    IReadOnlyList<ModerationMessageDto> Evidence,
    IReadOnlyList<ExplanationDto> Explanations,
    IReadOnlyList<WarningDto> Warnings,
    IReadOnlyList<BanRequestDto> BanRequests);

/// <summary>Мгновенно отключает открытые соединения пользователя (SignalR): после блокировки аккаунта.</summary>
public interface IRealtimeDisconnector
{
    Task DisconnectUserAsync(Guid userId, CancellationToken ct);
}
