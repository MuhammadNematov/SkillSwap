using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>Запись журнала аудита (раздел 10.2 ТЗ). <paramref name="Details"/> сохраняется как JSON (camelCase).</summary>
public sealed record AuditEntry(Guid? ActorId, string Action, string TargetType, Guid? TargetId, object? Details = null);

/// <summary>
/// Журнал аудита: только добавление (база запрещает изменение и удаление). <see cref="Add"/> кладёт запись в ту же транзакцию, что и действие,
/// которое она описывает (откат отменит и запись); <see cref="WriteAsync"/> записывает сразу — для чтения, которое ничего не меняет (запрос истории чата).
/// </summary>
public interface IAuditLog
{
    void Add(AuditEntry entry);

    Task WriteAsync(AuditEntry entry, CancellationToken ct);
}

/// <summary>Откуда пришёл запрос (адрес клиента для журнала аудита). Вне запроса (фоновая задача) адреса нет.</summary>
public interface IRequestInfo
{
    string? IpAddress { get; }
}

/// <summary>Названия действий в журнале аудита: единый список, по нему фильтруют журнал.</summary>
public static class AuditActions
{
    public const string ChatRead = "ChatRead";
    public const string ChatAccessGranted = "ChatAccessGranted";
    public const string ChatAccessRevoked = "ChatAccessRevoked";
    public const string CaseOpened = "CaseOpened";
    public const string CaseWithdrawn = "CaseWithdrawn";
    public const string CaseTaken = "CaseTaken";
    public const string ExplanationRequested = "ExplanationRequested";
    public const string ExplanationAnswered = "ExplanationAnswered";
    public const string ExplanationExpired = "ExplanationExpired";
    public const string CaseResolved = "CaseResolved";
    public const string CaseClosed = "CaseClosed";
    public const string ExplanationStopped = "ExplanationStopped";
    public const string CaseMaterialsRead = "CaseMaterialsRead";
    public const string CaseOverdue = "CaseOverdue";
    public const string ChatPurged = "ChatPurged";
    public const string EvidenceReleased = "EvidenceReleased";
    public const string NotificationsPurged = "NotificationsPurged";
    public const string BucketReconciled = "BucketReconciled";
    public const string CaseAppealed = "CaseAppealed";
    public const string AppealReviewAssigned = "AppealReviewAssigned";
    public const string AppealConcluded = "AppealConcluded";
    public const string AppealDecided = "AppealDecided";
    public const string WarningIssued = "WarningIssued";
    public const string WarningRevoked = "WarningRevoked";
    public const string ExchangeFrozen = "ExchangeFrozen";
    public const string ExchangeUnfrozen = "ExchangeUnfrozen";
    public const string ExchangeForceEnded = "ExchangeForceEnded";
    public const string BanRequested = "BanRequested";
    public const string BanDecided = "BanDecided";
    public const string ReviewHidden = "ReviewHidden";
    public const string ReviewRestored = "ReviewRestored";
    public const string CaseReleased = "CaseReleased";
    public const string AppealAdminReleased = "AppealAdminReleased";
    public const string AppealReviewReleased = "AppealReviewReleased";
}

/// <summary>Хранилище временных доступов модераторов к чату (таблица ChatAccessGrants).</summary>
public interface IChatAccessGrantStore
{
    void Add(ChatAccessGrant grant);

    /// <summary>Неотозванный доступ этого модератора к чату этого обмена; null, если его нет.</summary>
    Task<ChatAccessGrant?> FindActiveAsync(Guid moderatorId, Guid exchangeId, CancellationToken ct);

    /// <summary>Неотозванные доступы по обращению с отслеживанием: их отзывают при решении.</summary>
    Task<IReadOnlyList<ChatAccessGrant>> ListActiveForCaseAsync(Guid caseId, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}

/// <summary>
/// Кто может читать чат обмена (разделы 3 и 10.2 ТЗ): участники обмена и модератор с активным доступом (<c>ChatAccessGrant</c> без <c>RevokedAt</c>) именно к этому обмену.
/// Админ и суперадмин чат не читают никогда, даже если доступ каким-то образом записан на них. Служебный аккаунт участником обмена не бывает.
/// </summary>
public sealed class ChatAccessPolicy
{
    private readonly Exchanges.IExchangeStore _exchanges;
    private readonly IChatAccessGrantStore _grants;

    public ChatAccessPolicy(Exchanges.IExchangeStore exchanges, IChatAccessGrantStore grants)
    {
        _exchanges = exchanges;
        _grants = grants;
    }

    public async Task<bool> CanReadAsync(Guid userId, UserRole role, Guid exchangeId, CancellationToken ct)
    {
        var exchange = await _exchanges.GetViewAsync(exchangeId, ct);
        if (exchange is null)
        {
            return false;
        }

        if (exchange.UserA.Id == userId || exchange.UserB.Id == userId)
        {
            return true;
        }

        return role == UserRole.Moderator && await _grants.FindActiveAsync(userId, exchangeId, ct) is not null;
    }
}
