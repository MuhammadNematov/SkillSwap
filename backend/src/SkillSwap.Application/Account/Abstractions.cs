using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Account;

/// <summary>
/// Нужно ли удерживать личные данные пользователя после удаления аккаунта: пока по нему открыто обращение (раздел 8), данные не трогаем
/// до его закрытия. Обращения появятся на этапе 6; до тех пор реализация всегда отвечает «нет».
/// </summary>
public interface IErasureHold
{
    Task<bool> HasOpenCaseAsync(Guid userId, CancellationToken ct);
}

/// <summary>Массовые операции над данными аккаунта при удалении (выполняются в транзакции удаления).</summary>
public interface IAccountStore
{
    /// <summary>Анкета пользователя: навыки «Умею» и «Хочу изучать» и языки занятий.</summary>
    Task DeleteProfileDataAsync(Guid userId, CancellationToken ct);

    /// <summary>Все уведомления пользователя.</summary>
    Task DeleteNotificationsAsync(Guid userId, CancellationToken ct);

    /// <summary>В чужих уведомлениях имя этого пользователя (поле <c>userName</c> при <c>userId</c> = он) заменяется подписью удалённого.</summary>
    Task AnonymizeNotificationNamesAsync(Guid userId, string placeholder, CancellationToken ct);
}

public interface IDataExportStore
{
    void Add(DataExport export);

    Task<DataExport?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Последняя выгрузка пользователя (по времени запроса).</summary>
    Task<DataExport?> LastAsync(Guid userId, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}

/// <summary>Собирает JSON с данными пользователя (профиль, навыки, заявки, обмены, уроки, свои сообщения, отзывы) в поток.</summary>
public interface IUserDataExportSource
{
    Task WriteAsync(Guid userId, Stream output, DateTime now, CancellationToken ct);
}
