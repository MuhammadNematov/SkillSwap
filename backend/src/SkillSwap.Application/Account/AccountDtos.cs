using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Account;

/// <summary>Удаление аккаунта: пароль обязателен, код 2FA (или резервный код) обязателен, если 2FA включена.</summary>
public sealed record DeleteAccountRequest(string? Password, string? Code, bool IsRecoveryCode);

/// <summary>Выгрузка данных. Ссылка на скачивание приходит только на email и действует 24 часа.</summary>
/// <param name="ExpiresAt">До какого времени действует ссылка (у готовой выгрузки).</param>
public sealed record DataExportDto(Guid Id, DataExportStatus Status, DateTime RequestedAt, DateTime? ReadyAt, DateTime? ExpiresAt);
