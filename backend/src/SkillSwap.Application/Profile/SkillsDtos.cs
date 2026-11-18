using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Profile;

/// <summary>Навык в моей анкете. <c>isActive: false</c> — админ выключил навык после того, как вы его выбрали: уберите его из анкеты.</summary>
public sealed record UserSkillDto(
    Guid SkillId,
    Guid CategoryId,
    string NameTg,
    string NameRu,
    string NameEn,
    SkillLevel Level,
    string? Description,
    bool IsActive);

/// <summary>Навык в чужом профиле (только включённые).</summary>
public sealed record PublicSkillDto(
    Guid SkillId,
    Guid CategoryId,
    string NameTg,
    string NameRu,
    string NameEn,
    SkillLevel Level,
    string? Description);

/// <param name="IsComplete">Анкета полная: минимум 1 включённый навык в «Умею», 1 в «Хочу изучать» и хотя бы один язык занятий (профиль). Только тогда открыт подбор.</param>
/// <param name="IsSearchable">Показываетесь ли вы другим. Первое заполнение анкеты включает показ само; если анкета перестала быть полной, сервер скрывает вас сам.</param>
public sealed record MySkillsDto(
    IReadOnlyList<UserSkillDto> Teach,
    IReadOnlyList<UserSkillDto> Learn,
    bool IsComplete,
    bool IsSearchable);

public sealed record SkillEntryRequest(Guid SkillId, SkillLevel? Level, string? Description);

/// <summary>PUT заменяет анкету целиком: оба списка обязательны (пустой — очистить), чтобы пропуск не стёр данные молча.</summary>
public sealed record SaveSkillsRequest(List<SkillEntryRequest>? Teach, List<SkillEntryRequest>? Learn);
