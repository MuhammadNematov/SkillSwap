using SkillSwap.Domain.Common;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>Состояние запроса на привязку личного аккаунта, вычисляется по полям и времени.</summary>
public enum PersonalLinkStatus
{
    Pending,
    Confirmed,
    Declined,
    Cancelled,
    Expired,
}

/// <summary>
/// Запрос сотрудника привязать личный аккаунт к служебному (таблица PersonalLinkRequests, раздел 3 ТЗ, правило 3). Владение личным аккаунтом доказывается двумя вещами сразу:
/// доступом к его почте (ссылка приходит письмом) и входом в сам личный аккаунт (подтвердить может только он). Токен в базе только в виде SHA-256, срок 24 часа.
/// </summary>
public class PersonalLinkRequest : BaseEntity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private PersonalLinkRequest()
    {
    }

    /// <summary>Служебный аккаунт, который просит привязку.</summary>
    public Guid StaffId { get; private set; }

    /// <summary>Личный аккаунт, который нужно привязать.</summary>
    public Guid PersonalUserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? DeclinedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }

    public PersonalLinkStatus StatusAt(DateTime now)
    {
        if (ConfirmedAt is not null)
        {
            return PersonalLinkStatus.Confirmed;
        }

        if (DeclinedAt is not null)
        {
            return PersonalLinkStatus.Declined;
        }

        if (CancelledAt is not null)
        {
            return PersonalLinkStatus.Cancelled;
        }

        return now > ExpiresAt ? PersonalLinkStatus.Expired : PersonalLinkStatus.Pending;
    }

    public bool IsPending(DateTime now) => StatusAt(now) == PersonalLinkStatus.Pending;

    public static PersonalLinkRequest Create(Guid staffId, Guid personalUserId, string tokenHash, DateTime now)
    {
        if (staffId == personalUserId)
        {
            throw new DomainValidationException("link_same_account", "Служебный и личный аккаунты должны быть разными.");
        }

        if (string.IsNullOrWhiteSpace(tokenHash) || tokenHash.Length != 64)
        {
            throw new DomainValidationException("link_token_invalid", "Хеш токена: 64 символа (SHA-256 в hex).");
        }

        return new PersonalLinkRequest { StaffId = staffId, PersonalUserId = personalUserId, TokenHash = tokenHash, ExpiresAt = now + Lifetime };
    }

    /// <summary>Личный аккаунт подтвердил привязку. Только пока запрос действует.</summary>
    public void Confirm(DateTime now)
    {
        RequirePending(now);
        ConfirmedAt = now;
    }

    /// <summary>Владелец личного аккаунта отказался. Только пока запрос действует.</summary>
    public void Decline(DateTime now)
    {
        RequirePending(now);
        DeclinedAt = now;
    }

    /// <summary>Сотрудник отменил свой запрос (или его заменил новый). Повтор и отмена уже закрытого ничего не меняют.</summary>
    /// <returns>true, если отменено этим вызовом.</returns>
    public bool Cancel(DateTime now)
    {
        if (!IsPending(now))
        {
            return false;
        }

        CancelledAt = now;
        return true;
    }

    private void RequirePending(DateTime now)
    {
        switch (StatusAt(now))
        {
            case PersonalLinkStatus.Confirmed:
                throw new DomainValidationException("link_request_already_confirmed", "Привязка уже подтверждена.");
            case PersonalLinkStatus.Declined:
                throw new DomainValidationException("link_request_declined", "Вы уже отказались от этой привязки.");
            case PersonalLinkStatus.Cancelled:
                throw new DomainValidationException("link_request_cancelled", "Запрос отменён.");
            case PersonalLinkStatus.Expired:
                throw new DomainValidationException("link_request_expired", "Срок запроса (24 часа) истёк: пусть сотрудник отправит его заново.");
        }
    }
}
