using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Staff;

/// <summary>Одноразовые билеты на вход в панель фоновых задач. Реализация хранит билеты и сессии панели в памяти процесса.</summary>
public interface IDashboardTickets
{
    /// <summary>Панель подключена (фоновые задачи включены).</summary>
    bool Available { get; }

    /// <summary>Адрес панели без параметров, например <c>/hangfire</c>.</summary>
    string Path { get; }

    TimeSpan TicketLifetime { get; }

    TimeSpan SessionLifetime { get; }

    /// <summary>Выдаёт одноразовый билет для суперадмина. Им открывают панель один раз, после чего сервер ставит сессию.</summary>
    (string Ticket, DateTime ExpiresAt) Issue(Guid userId);
}

public sealed record OpenHangfirePanelRequest(StepUpConfirmation? Confirmation);

/// <param name="Url">Относительный адрес для открытия панели в новой вкладке (содержит одноразовый билет).</param>
/// <param name="TicketExpiresAt">До какого времени билет годится (после открытия или по истечении он недействителен).</param>
/// <param name="SessionMinutes">Сколько минут после открытия действует сессия панели.</param>
public sealed record HangfirePanelDto(string Url, DateTime TicketExpiresAt, int SessionMinutes);

/// <summary>
/// Панель фоновых задач Hangfire для суперадмина (раздел 3 ТЗ). Браузер не умеет присылать токен доступа в заголовке, поэтому вход двухшаговый: суперадмин с подтверждением паролем и 2FA
/// получает одноразовый билет (<see cref="OpenAsync"/>), открывает по нему панель, сервер ставит короткую сессию в защищённой cookie. Статус суперадмина проверяется по базе на каждый запрос панели.
/// Открытие панели пишется в аудит.
/// </summary>
public sealed class HangfirePanelService
{
    private readonly IDashboardTickets _tickets;
    private readonly IProfileStore _profiles;
    private readonly StepUpService _stepUp;
    private readonly IAuditLog _audit;

    public HangfirePanelService(IDashboardTickets tickets, IProfileStore profiles, StepUpService stepUp, IAuditLog audit)
    {
        _tickets = tickets;
        _profiles = profiles;
        _stepUp = stepUp;
        _audit = audit;
    }

    /// <summary>409 <c>dashboard_unavailable</c> (фоновые задачи выключены), 403 <c>superadmin_required</c> и ошибки подтверждения.</summary>
    public async Task<HangfirePanelDto> OpenAsync(Guid actorId, OpenHangfirePanelRequest request, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(actorId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role != UserRole.SuperAdmin || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("superadmin_required", "Доступно только суперадмину.");
        }

        if (!_tickets.Available)
        {
            throw new ConflictException("dashboard_unavailable", "Панель фоновых задач не подключена: фоновые задачи выключены в настройках сервера.");
        }

        await _stepUp.RequireAsync(actorId, request.Confirmation, ct);
        var (ticket, expires) = _tickets.Issue(actorId);
        await _audit.WriteAsync(new AuditEntry(actorId, StaffAuditActions.HangfirePanelOpened, "HangfirePanel", null), ct);
        return new HangfirePanelDto($"{_tickets.Path}?ticket={Uri.EscapeDataString(ticket)}", expires, (int)_tickets.SessionLifetime.TotalMinutes);
    }
}
