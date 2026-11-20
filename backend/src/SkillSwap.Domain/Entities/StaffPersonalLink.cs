using SkillSwap.Domain.Common;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// История привязок личных аккаунтов к служебному (таблица StaffPersonalLinks, раздел 3 ТЗ, правило 3). Запись создаётся при подтверждении привязки и не удаляется: проверка конфликта
/// интересов учитывает **все** личные аккаунты, когда-либо привязанные к сотруднику, и их бывших партнёров по обменам. Иначе сотрудник мог бы снять привязку, взять обращение своего
/// личного аккаунта и привязать обратно. <see cref="UnlinkedAt"/> заполняется, когда привязку сняли (это освобождает место под другой личный аккаунт, но не снимает конфликт).
/// </summary>
public class StaffPersonalLink : BaseEntity
{
    private StaffPersonalLink()
    {
    }

    public Guid StaffId { get; private set; }
    public Guid PersonalUserId { get; private set; }

    /// <summary>Когда привязка подтверждена (по часам приложения).</summary>
    public DateTime LinkedAt { get; private set; }

    public DateTime? UnlinkedAt { get; private set; }

    /// <summary>Кто снял привязку: админ или суперадмин.</summary>
    public Guid? UnlinkedById { get; private set; }

    public bool IsActive => UnlinkedAt is null;

    public static StaffPersonalLink Create(Guid staffId, Guid personalUserId, DateTime now)
    {
        if (staffId == personalUserId)
        {
            throw new DomainValidationException("link_same_account", "Служебный и личный аккаунты должны быть разными.");
        }

        return new StaffPersonalLink { StaffId = staffId, PersonalUserId = personalUserId, LinkedAt = now };
    }

    /// <summary>Снять привязку (только админом или суперадмином). Повтор ничего не меняет.</summary>
    /// <returns>true, если снято этим вызовом.</returns>
    public bool Unlink(Guid byId, DateTime now)
    {
        if (UnlinkedAt is not null)
        {
            return false;
        }

        UnlinkedAt = now;
        UnlinkedById = byId;
        return true;
    }
}

/// <summary>Состояние запроса сотрудника на снятие привязки.</summary>
public enum UnlinkRequestStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>
/// Запрос сотрудника на снятие привязки личного аккаунта (таблица PersonalUnlinkRequests). Сам сотрудник привязку снять не может: он описывает причину, решение принимает админ
/// (модераторов) или суперадмин (админов и других суперадминов) с подтверждением паролем и кодом 2FA. Одновременно у сотрудника не больше одного необработанного запроса.
/// </summary>
public class PersonalUnlinkRequest : BaseEntity
{
    public const int ReasonMinLength = 10;
    public const int ReasonMaxLength = 500;

    private PersonalUnlinkRequest()
    {
    }

    public Guid StaffId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public UnlinkRequestStatus Status { get; private set; } = UnlinkRequestStatus.Pending;
    public Guid? DecidedById { get; private set; }
    public DateTime? DecidedAt { get; private set; }

    public static PersonalUnlinkRequest Create(Guid staffId, string? reason) => new()
    {
        StaffId = staffId,
        Reason = Case.CleanText(reason, ReasonMinLength, ReasonMaxLength, "unlink_reason"),
    };

    public void Approve(Guid adminId, DateTime now) => Decide(UnlinkRequestStatus.Approved, adminId, now);

    public void Reject(Guid adminId, DateTime now) => Decide(UnlinkRequestStatus.Rejected, adminId, now);

    private void Decide(UnlinkRequestStatus to, Guid adminId, DateTime now)
    {
        if (Status != UnlinkRequestStatus.Pending)
        {
            throw new InvalidStateTransitionException(nameof(PersonalUnlinkRequest), Status.ToString(), to.ToString());
        }

        Status = to;
        DecidedById = adminId;
        DecidedAt = now;
    }
}
