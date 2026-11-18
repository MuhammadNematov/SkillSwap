using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Обмен (таблица Exchanges, разделы 6 и 9.2 ТЗ). Участник A — отправитель заявки, участник B — получатель.
/// SkillA — то, чему учит A, SkillB — то, чему учит B. Все переходы статуса — только методами этого класса.
/// </summary>
public class Exchange : BaseEntity
{
    public const int AbortReasonMaxLength = 500;
    public const int ExtensionDays = 30;

    /// <summary>Причина прерывания при блокировке одного из участников (виновного нет).</summary>
    public const string AbortReasonBlocked = "blocked";

    private Exchange()
    {
    }

    public Guid UserAId { get; private set; }
    public Guid UserBId { get; private set; }
    public Guid SkillAId { get; private set; }
    public Guid SkillBId { get; private set; }
    public Guid RequestId { get; private set; }
    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }
    public bool IsExtended { get; private set; }
    public ExchangeStatus Status { get; private set; }
    public bool CompletedByA { get; private set; }
    public bool CompletedByB { get; private set; }

    /// <summary>Согласие участника на продление: продление применяется, когда согласны оба.</summary>
    public bool ExtensionAcceptedByA { get; private set; }

    public bool ExtensionAcceptedByB { get; private set; }
    public DateTime? FrozenAt { get; private set; }

    /// <summary>Кто прервал обмен; null, если прервала система (блокировка) или модератор.</summary>
    public Guid? AbortedById { get; private set; }

    public string? AbortReason { get; private set; }
    public DateTime? ClosedAt { get; private set; }

    public static Exchange Create(Guid requestId, Guid userAId, Guid userBId, Guid skillAId, Guid skillBId, int durationDays, DateTime now)
    {
        if (userAId == userBId)
        {
            throw new DomainValidationException("cannot_exchange_with_self", "Обмен возможен только между двумя разными людьми.");
        }

        if (durationDays <= 0)
        {
            throw new DomainValidationException("invalid_duration", "Срок обмена должен быть положительным.");
        }

        return new Exchange
        {
            UserAId = userAId, UserBId = userBId, SkillAId = skillAId, SkillBId = skillBId, RequestId = requestId,
            StartDate = now, EndDate = now.AddDays(durationDays), Status = ExchangeStatus.Active,
        };
    }

    public bool IsParticipant(Guid userId) => userId == UserAId || userId == UserBId;

    public Guid PartnerOf(Guid userId) => userId == UserAId ? UserBId : UserAId;

    /// <summary>Участник нажал «Завершить». Когда нажали оба, обмен завершается. Повторное нажатие тем же участником ничего не меняет.</summary>
    /// <returns>true, если обмен завершён этим вызовом.</returns>
    public bool MarkCompleted(Guid userId, DateTime now)
    {
        RequireParticipant(userId);
        RequireStatus(ExchangeStatus.Completed, ExchangeStatus.Active);

        if (userId == UserAId)
        {
            CompletedByA = true;
        }
        else
        {
            CompletedByB = true;
        }

        if (!(CompletedByA && CompletedByB))
        {
            return false;
        }

        Close(ExchangeStatus.Completed, now);
        return true;
    }

    /// <summary>Истёк срок (фоновая задача). Если срок ещё не вышел, ничего не делает и возвращает false.</summary>
    public bool CompleteIfDue(DateTime now)
    {
        if (Status != ExchangeStatus.Active || now < EndDate)
        {
            return false;   // замороженный обмен по сроку не завершается (раздел 9.2 ТЗ)
        }

        Close(ExchangeStatus.Completed, now);
        return true;
    }

    /// <summary>Участник вышел досрочно с причиной (только из Active).</summary>
    public void Abort(Guid userId, string? reason, DateTime now)
    {
        RequireParticipant(userId);
        RequireStatus(ExchangeStatus.Aborted, ExchangeStatus.Active);

        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new DomainValidationException("abort_reason_required", "Укажите причину.");
        }

        if (text.Length > AbortReasonMaxLength)
        {
            throw new DomainValidationException("abort_reason_too_long", $"Причина длиннее {AbortReasonMaxLength} символов.");
        }

        AbortedById = userId;
        AbortReason = text;
        Close(ExchangeStatus.Aborted, now);
    }

    /// <summary>Один из участников заблокировал другого: обмен прерывается без виновного.</summary>
    public void AbortByBlock(DateTime now)
    {
        RequireStatus(ExchangeStatus.Aborted, ExchangeStatus.Active);
        AbortReason = AbortReasonBlocked;
        Close(ExchangeStatus.Aborted, now);
    }

    /// <summary>Модератор или админ закрыл обмен (из Active или Frozen).</summary>
    public void AbortByModerator(string reason, DateTime now)
    {
        RequireStatus(ExchangeStatus.Aborted, ExchangeStatus.Active, ExchangeStatus.Frozen);
        AbortReason = string.IsNullOrWhiteSpace(reason) ? "moderation" : reason.Trim();
        Close(ExchangeStatus.Aborted, now);
    }

    public void Freeze(DateTime now)
    {
        RequireStatus(ExchangeStatus.Frozen, ExchangeStatus.Active);
        Status = ExchangeStatus.Frozen;
        FrozenAt = now;
    }

    /// <summary>Разморозка: срок не шёл, поэтому <see cref="EndDate"/> сдвигается на время заморозки.</summary>
    public void Unfreeze(DateTime now)
    {
        RequireStatus(ExchangeStatus.Active, ExchangeStatus.Frozen);
        EndDate += now - FrozenAt!.Value;
        FrozenAt = null;
        Status = ExchangeStatus.Active;
    }

    /// <summary>
    /// Участник согласен продлить обмен на 30 дней. Продление одно за обмен; применяется, когда согласны оба.
    /// Статус не меняется, меняются <see cref="EndDate"/> и <see cref="IsExtended"/>.
    /// </summary>
    /// <returns>true, если продление применено этим вызовом.</returns>
    public bool AcceptExtension(Guid userId)
    {
        RequireParticipant(userId);
        RequireStatus(ExchangeStatus.Active, ExchangeStatus.Active);
        if (IsExtended)
        {
            throw new DomainValidationException("already_extended", "Обмен уже продлевался: продление возможно один раз.");
        }

        if (userId == UserAId)
        {
            ExtensionAcceptedByA = true;
        }
        else
        {
            ExtensionAcceptedByB = true;
        }

        if (!(ExtensionAcceptedByA && ExtensionAcceptedByB))
        {
            return false;
        }

        EndDate = EndDate.AddDays(ExtensionDays);
        IsExtended = true;
        ExtensionAcceptedByA = false;
        ExtensionAcceptedByB = false;
        return true;
    }

    private void Close(ExchangeStatus to, DateTime now)
    {
        Status = to;
        ClosedAt = now;
    }

    private void RequireParticipant(Guid userId)
    {
        if (!IsParticipant(userId))
        {
            throw new DomainValidationException("not_participant", "Вы не участник этого обмена.");
        }
    }

    private void RequireStatus(ExchangeStatus to, params ExchangeStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
        {
            throw new InvalidStateTransitionException(nameof(Exchange), Status.ToString(), to.ToString());
        }
    }
}
