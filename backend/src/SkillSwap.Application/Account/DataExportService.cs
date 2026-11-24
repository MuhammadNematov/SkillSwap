using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Files;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Account;

/// <summary>
/// Выгрузка данных (POST /me/export, раздел 8.1 ТЗ): запрос ставит фоновую задачу, она собирает JSON (профиль, навыки, заявки, обмены, уроки,
/// свои сообщения расшифрованными, отзывы), кладёт его в закрытый бакет и шлёт на email ссылку на 24 часа. Не чаще одного раза в сутки.
/// </summary>
public sealed class DataExportService
{
    private readonly IDataExportStore _exports;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly TimeProvider _clock;

    public DataExportService(IDataExportStore exports, IProfileStore profiles, IUnitOfWork uow, IBackgroundJobs jobs, TimeProvider clock)
    {
        _exports = exports;
        _profiles = profiles;
        _uow = uow;
        _jobs = jobs;
        _clock = clock;
    }

    public async Task<DataExportDto> RequestAsync(Guid userId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);

        var export = await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([userId], token);   // два одновременных запроса не обойдут «раз в сутки»
            var now = _clock.GetUtcNow().UtcDateTime;
            var last = await _exports.LastAsync(userId, token);
            if (last is { Status: not DataExportStatus.Failed } && last.CreatedAt + DataExport.MinInterval > now)
            {
                throw new TooManyRequestsException(
                    "export_rate_limited", "Выгрузку можно запрашивать не чаще одного раза в сутки.");
            }

            var created = DataExport.Request(userId);
            _exports.Add(created);
            await _exports.SaveAsync(token);
            return created;
        }, ct);

        var id = export.Id;
        _jobs.Enqueue<DataExportJobs>(j => j.BuildAsync(id));
        return ToDto(export);
    }

    /// <summary>Состояние последней выгрузки. Ссылка в ответ не попадает: она приходит только на email.</summary>
    public async Task<DataExportDto> LastAsync(Guid userId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var last = await _exports.LastAsync(userId, ct) ?? throw new NotFoundException("export_not_found", "Выгрузку ещё не запрашивали.");
        return ToDto(last);
    }

    internal static DataExportDto ToDto(DataExport e) => new(e.Id, e.Status, e.CreatedAt, e.ReadyAt, e.ExpiresAt);

    private async Task RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Выгрузка доступна только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }
}

/// <summary>Фоновые задачи выгрузки. Обе идемпотентны: повторный запуск ничего не портит.</summary>
public sealed class DataExportJobs
{
    private readonly IDataExportStore _exports;
    private readonly IUserDataExportSource _source;
    private readonly IFileStorage _storage;
    private readonly IProfileStore _profiles;
    private readonly IMailSender _mail;
    private readonly IBackgroundJobs _jobs;
    private readonly TimeProvider _clock;
    private readonly FrontendOptions _frontend;
    private readonly ILogger<DataExportJobs> _logger;

    public DataExportJobs(
        IDataExportStore exports, IUserDataExportSource source, IFileStorage storage, IProfileStore profiles, IMailSender mail,
        IBackgroundJobs jobs, TimeProvider clock, IOptions<FrontendOptions> frontend, ILogger<DataExportJobs> logger)
    {
        _exports = exports;
        _source = source;
        _storage = storage;
        _profiles = profiles;
        _mail = mail;
        _jobs = jobs;
        _clock = clock;
        _frontend = frontend.Value;
        _logger = logger;
    }

    /// <summary>Собирает выгрузку и отправляет ссылку. Сбой помечает выгрузку Failed (повторить запрос можно сразу).</summary>
    public async Task BuildAsync(Guid exportId)
    {
        var ct = CancellationToken.None;
        var export = await _exports.FindAsync(exportId, ct);
        if (export is null || !export.Start())
        {
            return;
        }

        await _exports.SaveAsync(ct);
        try
        {
            var user = await _profiles.FindUserAsync(export.UserId, ct);
            if (user is null || user.Status != UserStatus.Active || string.IsNullOrEmpty(user.Email))
            {
                export.Fail();   // аккаунт успели удалить: отдавать нечего
                await _exports.SaveAsync(ct);
                return;
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            using var buffer = new MemoryStream();
            await _source.WriteAsync(export.UserId, buffer, now, ct);
            buffer.Position = 0;
            var key = DataExport.KeyFor(export.UserId, export.Id);
            await _storage.PutAsync(key, buffer, "application/json", ct);

            export.Complete(buffer.Length, now);
            await _exports.SaveAsync(ct);

            // Ссылка уходит письмом, поэтому нужна полная (в режиме относительных ссылок хранилище отдаёт путь без адреса)
            var link = FileLinks.Absolute(_storage.CreateDownloadUrl(key, "skillswap-data.json", "application/json", DataExport.LinkLifetime), _frontend);
            var (subject, body) = EmailTemplates.DataExportReady(user.UiLanguage, user.Name, link);
            await _mail.SendAsync(user.Email, subject, body, ct);

            var id = export.Id;
            _jobs.Schedule<DataExportJobs>(j => j.ExpireAsync(id), new DateTimeOffset(export.ExpiresAt!.Value, TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Не удалось собрать выгрузку данных {ExportId}", exportId);
            export.Fail();
            await _exports.SaveAsync(ct);
        }
    }

    /// <summary>Через 24 часа файл выгрузки удаляется из хранилища, статус Expired.</summary>
    public async Task ExpireAsync(Guid exportId)
    {
        var export = await _exports.FindAsync(exportId, CancellationToken.None);
        if (export is null || !export.ExpireIfDue(_clock.GetUtcNow().UtcDateTime))
        {
            return;
        }

        await _storage.DeleteAsync(export.StorageKey!, CancellationToken.None);
        await _exports.SaveAsync(CancellationToken.None);
    }
}
