using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Chat;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Exchanges;

/// <summary>Выход из обмена: причина обязательна (до 500 символов).</summary>
public sealed record AbortExchangeRequest(string? Reason);

/// <summary>Баланс уроков: сколько проведено в роли преподавателя у меня и у партнёра.</summary>
/// <param name="ShowReminder">Разница больше 3: обоим показывается мягкое напоминание (раздел 6.1 ТЗ).</param>
public sealed record ExchangeBalanceDto(int MyLessons, int PartnerLessons, bool ShowReminder);

/// <summary>Обмен глазами участника: «я» и «партнёр».</summary>
/// <param name="ISkillTeach">Чему учу я.</param>
/// <param name="ISkillLearn">Чему учит партнёр (то, что изучаю я).</param>
/// <param name="MyExtensionAccepted">Я согласился на продление и жду партнёра.</param>
/// <param name="AbortedBy">me, partner или null (прервала система при блокировке либо модератор).</param>
public sealed record ExchangeDto(
    Guid Id,
    ExchangeStatus Status,
    RequestUserDto Partner,
    RequestSkillDto ISkillTeach,
    RequestSkillDto ISkillLearn,
    Guid RequestId,
    DateTime StartDate,
    DateTime EndDate,
    bool IsExtended,
    bool CompletedByMe,
    bool CompletedByPartner,
    bool MyExtensionAccepted,
    bool PartnerExtensionAccepted,
    DateTime? FrozenAt,
    string? AbortedBy,
    string? AbortReason,
    DateTime? ClosedAt,
    ExchangeBalanceDto Balance,
    DateTime CreatedAt,
    ChatStateDto Chat,
    int UnreadMessages);

/// <summary>Обмен из хранилища с людьми и навыками обеих сторон.</summary>
public sealed record ExchangeRow(
    Guid Id,
    ExchangeStatus Status,
    RequestUserDto UserA,
    RequestUserDto UserB,
    RequestSkillDto SkillA,
    RequestSkillDto SkillB,
    Guid RequestId,
    DateTime StartDate,
    DateTime EndDate,
    bool IsExtended,
    bool CompletedByA,
    bool CompletedByB,
    bool ExtensionAcceptedByA,
    bool ExtensionAcceptedByB,
    DateTime? FrozenAt,
    Guid? AbortedById,
    Guid? BlockedById,
    string? AbortReason,
    DateTime? ClosedAt,
    DateTime CreatedAt);

/// <summary>Баланс уроков (раздел 6.1 ТЗ): подсказка, если разница больше 3.</summary>
public static class ExchangeBalance
{
    public const int ReminderThreshold = 3;

    public static ExchangeBalanceDto Calculate(int mine, int partner) =>
        new(mine, partner, Math.Abs(mine - partner) > ReminderThreshold);
}
