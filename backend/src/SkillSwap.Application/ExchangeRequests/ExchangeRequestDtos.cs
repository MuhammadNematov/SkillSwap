using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.ExchangeRequests;

/// <summary>Создание заявки. Срок 30 или 60 дней, сообщение необязательное (до 1000 символов).</summary>
/// <param name="ToUserId">Кому отправляем.</param>
/// <param name="ToTeachesSkillId">Навык получателя (из его «Умею»), которому хотим научиться. Обязателен.</param>
/// <param name="FromTeachesSkillId">Мой навык (из моего «Умею»), которому научу взамен. Можно не указывать: получатель выберет при принятии.</param>
public sealed record CreateExchangeRequestRequest(Guid ToUserId, Guid ToTeachesSkillId, Guid? FromTeachesSkillId, int DurationDays, string? Message);

/// <summary>Принятие заявки. <c>FromTeachesSkillId</c> обязателен, если отправитель не указал свой навык.</summary>
public sealed record AcceptExchangeRequestRequest(Guid? FromTeachesSkillId);

/// <param name="IsDeleted">Пользователь удалил аккаунт: имя заменено подписью «Удалённый пользователь» (клиент показывает её на своём языке), аватара нет.</param>
public sealed record RequestUserDto(Guid Id, string Name, string? AvatarUrl, bool IsDeleted = false);

public sealed record RequestSkillDto(Guid Id, string NameTg, string NameRu, string NameEn);

/// <param name="Direction">in — мне прислали, out — я отправил.</param>
/// <param name="FromTeachesSkill">Навык отправителя; null, пока получатель не выбрал его при принятии.</param>
/// <param name="ExchangeId">Обмен, созданный при принятии заявки; null, пока заявка не принята.</param>
public sealed record ExchangeRequestDto(
    Guid Id,
    string Direction,
    ExchangeRequestStatus Status,
    RequestUserDto FromUser,
    RequestUserDto ToUser,
    RequestSkillDto? FromTeachesSkill,
    RequestSkillDto ToTeachesSkill,
    int DurationDays,
    string? Message,
    DateTime ExpiresAt,
    DateTime? RespondedAt,
    DateTime CreatedAt,
    Guid? ExchangeId);

/// <summary>Строка из хранилища: направление добавляет сервис.</summary>
public sealed record ExchangeRequestRow(
    Guid Id,
    ExchangeRequestStatus Status,
    RequestUserDto FromUser,
    RequestUserDto ToUser,
    RequestSkillDto? FromTeachesSkill,
    RequestSkillDto ToTeachesSkill,
    int DurationDays,
    string? Message,
    DateTime ExpiresAt,
    DateTime? RespondedAt,
    DateTime CreatedAt,
    Guid? ExchangeId);
