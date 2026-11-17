namespace SkillSwap.Application.Abstractions;

/// <summary>Резервные коды 2FA. Хранилище видит только хеши (<c>RecoveryCodes.Hash</c>), открытых кодов у него нет.</summary>
public interface IRecoveryCodeStore
{
    /// <summary>Заменяет все коды пользователя новым набором (старые, в том числе неиспользованные, перестают работать).</summary>
    Task ReplaceAsync(Guid userId, IReadOnlyCollection<string> codeHashes, CancellationToken ct = default);

    /// <summary>
    /// Атомарно гасит код: true, если он существовал и ещё не был использован. Два параллельных запроса
    /// с одним кодом не пройдут оба.
    /// </summary>
    Task<bool> TryRedeemAsync(Guid userId, string codeHash, CancellationToken ct = default);

    Task DeleteAllAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Последний использованный шаг TOTP: защита от повторного использования кода.</summary>
public interface ITwoFactorStepStore
{
    /// <summary>
    /// Атомарно запоминает шаг, если он строго больше последнего использованного.
    /// false — этот или более новый шаг уже использован (повтор кода).
    /// </summary>
    Task<bool> TryUseStepAsync(Guid userId, long step, CancellationToken ct = default);

    /// <summary>Сбрасывает память о шагах (при смене или удалении ключа аутентификатора).</summary>
    Task ResetAsync(Guid userId, CancellationToken ct = default);
}
