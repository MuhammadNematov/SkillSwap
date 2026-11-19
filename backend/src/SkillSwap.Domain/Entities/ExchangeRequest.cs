using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Заявка на обмен (таблица ExchangeRequests, разделы 5.3 и 9.1 ТЗ). Отправитель (From) хочет научиться тому, что умеет получатель (To),
/// и предлагает взамен свой навык. Навык получателя обязателен, навык отправителя можно не указывать:
/// тогда получатель выберет его из «Умею» отправителя при принятии. Все переходы статуса — только методами этого класса.
/// </summary>
public class ExchangeRequest : BaseEntity
{
    public const int MessageMaxLength = 1000;
    public const int ValidDays = 7;
    public static readonly int[] AllowedDurations = [30, 60];

    private ExchangeRequest()
    {
    }

    public Guid FromUserId { get; private set; }
    public Guid ToUserId { get; private set; }

    /// <summary>Навык, которому отправитель научит получателя. Null — отправитель не указал, получатель выберет при принятии.</summary>
    public Guid? FromTeachesSkillId { get; private set; }

    /// <summary>Навык, которому получатель научит отправителя. Обязателен.</summary>
    public Guid ToTeachesSkillId { get; private set; }

    public int DurationDays { get; private set; }
    public string? Message { get; private set; }
    public ExchangeRequestStatus Status { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RespondedAt { get; private set; }

    public static ExchangeRequest Create(
        Guid fromUserId, Guid toUserId, Guid? fromTeachesSkillId, Guid toTeachesSkillId, int durationDays, string? message, DateTime now)
    {
        if (fromUserId == toUserId)
        {
            throw new DomainValidationException("cannot_request_self", "Нельзя отправить заявку самому себе.");
        }

        if (!AllowedDurations.Contains(durationDays))
        {
            throw new DomainValidationException("invalid_duration", "Срок обмена: 30 или 60 дней.");
        }

        var text = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (text is { Length: > MessageMaxLength })
        {
            throw new DomainValidationException("message_too_long", $"Сообщение длиннее {MessageMaxLength} символов.");
        }

        return new ExchangeRequest
        {
            FromUserId = fromUserId,
            ToUserId = toUserId,
            FromTeachesSkillId = fromTeachesSkillId,
            ToTeachesSkillId = toTeachesSkillId,
            DurationDays = durationDays,
            Message = text,
            Status = ExchangeRequestStatus.Pending,
            ExpiresAt = now.AddDays(ValidDays),
        };
    }

    /// <summary>Заявка ещё ждёт ответа и срок не вышел.</summary>
    public bool IsOpenAt(DateTime now) => Status == ExchangeRequestStatus.Pending && now < ExpiresAt;

    /// <summary>
    /// Получатель принимает. Если отправитель не указал свой навык, <paramref name="chosenFromSkillId"/> обязателен;
    /// если указал, выбрать другой нельзя.
    /// </summary>
    public void Accept(Guid? chosenFromSkillId, DateTime now)
    {
        if (Status != ExchangeRequestStatus.Pending)
        {
            throw Invalid(ExchangeRequestStatus.Accepted);
        }

        if (FromTeachesSkillId is null)
        {
            FromTeachesSkillId = chosenFromSkillId
                ?? throw new DomainValidationException("from_skill_required", "Выберите навык отправителя из его списка «Умею».");
        }
        else if (chosenFromSkillId is not null && chosenFromSkillId != FromTeachesSkillId)
        {
            throw new DomainValidationException("from_skill_fixed", "Отправитель уже указал свой навык, заменить его нельзя.");
        }

        Status = ExchangeRequestStatus.Accepted;
        RespondedAt = now;
    }

    public void Decline(DateTime now) => Close(ExchangeRequestStatus.Declined, now);

    public void Withdraw(DateTime now) => Close(ExchangeRequestStatus.Withdrawn, now);

    /// <summary>Истёк срок, либо кто-то из двоих заблокирован или удалён.</summary>
    public void Expire(DateTime now) => Close(ExchangeRequestStatus.Expired, now);

    private void Close(ExchangeRequestStatus to, DateTime now)
    {
        if (Status != ExchangeRequestStatus.Pending)
        {
            throw Invalid(to);
        }

        Status = to;
        RespondedAt = now;
    }

    private InvalidStateTransitionException Invalid(ExchangeRequestStatus to) =>
        new(nameof(ExchangeRequest), Status.ToString(), to.ToString());
}
