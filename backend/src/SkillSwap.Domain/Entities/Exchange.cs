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

    /// <summary>Причина прерывания при удалении аккаунта одного из участников (виновного нет). Партнёр видит нейтральную <c>partner_unavailable</c>.</summary>
    public const string AbortReasonAccountDeleted = "account_deleted";

    /// <summary>Аккаунт одного из участников заблокирован администратором (одобренный запрос на бан).</summary>
    public const string AbortReasonAccountBlocked = "account_blocked";

    /// <summary>
    /// Причина прерывания, когда пострадавший выходит после трёх окончательных неявок партнёра (раздел 6.3 ТЗ). Участники видят эту причину обоим;
    /// виновным записывается партнёр (см. <see cref="AbortForPartnerNoShow"/>). Свободный текст причины этим значением не становится: признак
    /// хранится отдельно (<see cref="AbortedForNoShow"/>) и ставится только этим методом.
    /// </summary>
    public const string AbortReasonPartnerNoShow = "partner_no_show";

    /// <summary>Сколько окончательных неявок партнёра нужно, чтобы пострадавший мог выйти без последствий для себя.</summary>
    public const int NoShowAbortThreshold = 3;

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

    /// <summary>
    /// Когда очистка чатов удалила все сообщения и вложения обмена (через 90 дней после закрытия, раздел 10.3 ТЗ). Пока сообщения-доказательства обращения хранятся,
    /// остаётся null: очистка вернётся к обмену, когда доказательства отпустят. По этому признаку очистка не обходит один и тот же обмен каждый день.
    /// </summary>
    public DateTime? ChatPurgedAt { get; private set; }

    public void MarkChatPurged(DateTime now) => ChatPurgedAt ??= now;

    /// <summary>Кто прервал обмен; null, если прервала система (блокировка) или модератор.</summary>
    public Guid? AbortedById { get; private set; }

    public string? AbortReason { get; private set; }

    /// <summary>Обмен прерван пострадавшим после трёх неявок партнёра (<see cref="AbortForPartnerNoShow"/>). Основание для одностороннего отзыва на этапе 6.</summary>
    public bool AbortedForNoShow { get; private set; }

    /// <summary>Когда участникам разослали «можно оставить отзыв» (раздел 8.1 ТЗ): один раз на обмен, повторный запуск задачи ничего не шлёт.</summary>
    public DateTime? ReviewInvitesSentAt { get; private set; }

    /// <summary>Когда разослали напоминание «срок отзыва заканчивается» (за 2 дня).</summary>
    public DateTime? ReviewReminderSentAt { get; private set; }

    /// <returns>true, если отметка поставлена этим вызовом (рассылать можно); false — уже рассылали.</returns>
    public bool MarkReviewInvitesSent(DateTime now)
    {
        if (ReviewInvitesSentAt is not null)
        {
            return false;
        }

        ReviewInvitesSentAt = now;
        return true;
    }

    public bool MarkReviewReminderSent(DateTime now)
    {
        if (ReviewReminderSentAt is not null)
        {
            return false;
        }

        ReviewReminderSentAt = now;
        return true;
    }

    /// <summary>Кто заблокировал партнёра, из-за чего обмен прерван. В базе причина хранится настоящей (для модератора), участникам она показывается по-разному.</summary>
    public Guid? BlockedById { get; private set; }
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

    /// <summary>
    /// Обмен прерван пострадавшим из-за неявок партнёра: после трёх окончательных неявок (раздел 6.3 ТЗ). Выход «без последствий для себя»:
    /// виновным (<see cref="AbortedById"/>) записывается партнёр, а не вышедший. Признак <see cref="AbortedForNoShow"/> остаётся навсегда:
    /// на нём этап 6 строит одностороннее право пострадавшего на отзыв (раздел 8.1 ТЗ, пункт 3).
    /// </summary>
    /// <param name="victimId">Кто выходит.</param>
    /// <param name="partnerFinalNoShows">Сколько окончательных неявок у партнёра в этом обмене.</param>
    public void AbortForPartnerNoShow(Guid victimId, int partnerFinalNoShows, DateTime now)
    {
        RequireParticipant(victimId);
        RequireStatus(ExchangeStatus.Aborted, ExchangeStatus.Active);
        if (partnerFinalNoShows < NoShowAbortThreshold)
        {
            throw new DomainValidationException(
                "no_show_threshold_not_reached", $"Выйти без последствий можно после {NoShowAbortThreshold} неявок партнёра: сейчас {partnerFinalNoShows}.");
        }

        AbortedById = PartnerOf(victimId);
        AbortReason = AbortReasonPartnerNoShow;
        AbortedForNoShow = true;
        Close(ExchangeStatus.Aborted, now);
    }

    /// <summary>Один из участников заблокировал другого: обмен прерывается без виновного.</summary>
    public void AbortByBlock(Guid blockerId, DateTime now)
    {
        RequireStatus(ExchangeStatus.Aborted, ExchangeStatus.Active);
        BlockedById = blockerId;   // кто заблокировал: нужен, чтобы показать заблокированному нейтральную причину
        AbortReason = AbortReasonBlocked;
        Close(ExchangeStatus.Aborted, now);
    }

    /// <summary>Один из участников удалил аккаунт: активный или замороженный обмен прерывается без виновного.</summary>
    public void AbortByDeletion(DateTime now)
    {
        RequireStatus(ExchangeStatus.Aborted, ExchangeStatus.Active, ExchangeStatus.Frozen);
        AbortReason = AbortReasonAccountDeleted;
        Close(ExchangeStatus.Aborted, now);
    }

    /// <summary>Аккаунт участника заблокирован администратором: активный или замороженный обмен прерывается без виновного (партнёру причина показывается нейтрально).</summary>
    public void AbortByBan(DateTime now)
    {
        RequireStatus(ExchangeStatus.Aborted, ExchangeStatus.Active, ExchangeStatus.Frozen);
        AbortReason = AbortReasonAccountBlocked;
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
