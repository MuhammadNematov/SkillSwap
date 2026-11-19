namespace SkillSwap.Application.Abstractions;

/// <summary>
/// Код подтверждения email (шесть цифр, приходит в письме вместе со ссылкой). Все операции атомарные: попытка списывается ДО сравнения,
/// поэтому параллельный перебор не получит больше попыток, чем разрешено.
/// </summary>
public interface IEmailCodeStore
{
    /// <summary>Выдаёт новый код: прежний перестаёт действовать, счётчик попыток обнуляется.</summary>
    Task SetAsync(Guid userId, string codeHash, DateTime expiresAt, CancellationToken ct = default);

    /// <summary>
    /// Списывает одну попытку и возвращает хеш действующего кода; null, если кода нет, он истёк или попытки кончились.
    /// Сравнивать с введённым значением должен вызывающий.
    /// </summary>
    Task<string?> TakeAttemptAsync(Guid userId, DateTime now, int maxAttempts, CancellationToken ct = default);

    /// <summary>Убирает код (после успешного подтверждения любым способом).</summary>
    Task ClearAsync(Guid userId, CancellationToken ct = default);
}
