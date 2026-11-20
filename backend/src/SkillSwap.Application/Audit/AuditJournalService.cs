using System.Text.Json;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Audit;

/// <param name="Details">Подробности действия (объект JSON, поля зависят от действия); null, если запись без подробностей.</param>
/// <param name="IpAddress">Адрес, с которого сделано действие. Видит только суперадмин, у админа null.</param>
public sealed record AuditEntryDto(
    Guid Id, Guid? ActorId, string? ActorName, UserRole? ActorRole, string Action, string TargetType, Guid? TargetId, JsonElement? Details, string? IpAddress, DateTime CreatedAt);

public sealed record AuditPageDto(IReadOnlyList<AuditEntryDto> Items, int Page, int PageSize, int TotalCount);

/// <param name="From">Не раньше (включительно, UTC).</param>
/// <param name="To">Раньше (не включая, UTC).</param>
public sealed record AuditQuery(Guid? ActorId, string? Action, string? TargetType, Guid? TargetId, DateTime? From, DateTime? To, int Page, int PageSize);

/// <summary>Выборки журнала аудита. <paramref name="limitedTo"/> — админ: записи модераторов и его собственные; null — всё (суперадмин).</summary>
public interface IAuditJournalStore
{
    Task<(IReadOnlyList<AuditLog> Items, int Total)> SearchAsync(Guid? limitedTo, AuditQuery query, CancellationToken ct);

    /// <summary>Какие действия встречаются в видимой части журнала (для фильтра на экране).</summary>
    Task<IReadOnlyList<string>> ListActionsAsync(Guid? limitedTo, CancellationToken ct);
}

/// <summary>
/// Журнал аудита для панелей (раздел 3 и 10.2 ТЗ). Суперадмин видит всё (в том числе действия системы и адреса), админ только действия модераторов и свои (без адресов),
/// модератор и пользователь журнал не видят. Записи только читаются: изменить или удалить их нельзя даже на уровне базы.
/// </summary>
public sealed class AuditJournalService
{
    private readonly IAuditJournalStore _store;
    private readonly IProfileStore _profiles;

    public AuditJournalService(IAuditJournalStore store, IProfileStore profiles)
    {
        _store = store;
        _profiles = profiles;
    }

    /// <summary>
    /// Записи журнала, новые сверху. <paramref name="superAdminRoute"/> — запрос на маршруте суперадмина (нужна роль суперадмина); на маршруте админа суперадмин тоже видит всё.
    /// 400 <c>invalid_paging</c>, <c>invalid_period</c> (начало позже конца), <c>invalid_filter</c> (слишком длинное значение).
    /// </summary>
    public async Task<AuditPageDto> ListAsync(Guid viewerId, AuditQuery query, bool superAdminRoute, CancellationToken ct)
    {
        Paging.Validate(query.Page, query.PageSize);
        if (query.From is { } from && query.To is { } to && from >= to)
        {
            throw new BadRequestException("invalid_period", "Начало периода должно быть раньше конца.");
        }

        if ((query.Action?.Length ?? 0) > AuditLog.ActionMaxLength || (query.TargetType?.Length ?? 0) > AuditLog.TargetTypeMaxLength)
        {
            throw new BadRequestException("invalid_filter", "Значение фильтра слишком длинное.");
        }

        var viewer = await RequireViewerAsync(viewerId, superAdminRoute, ct);
        var full = viewer.Role == UserRole.SuperAdmin;
        var normalized = query with
        {
            Action = string.IsNullOrWhiteSpace(query.Action) ? null : query.Action.Trim(),
            TargetType = string.IsNullOrWhiteSpace(query.TargetType) ? null : query.TargetType.Trim(),
            From = AsUtc(query.From),
            To = AsUtc(query.To),
        };
        var (rows, total) = await _store.SearchAsync(full ? null : viewerId, normalized, ct);

        var people = await _profiles.FindUsersAsync(rows.Where(r => r.ActorId is not null).Select(r => r.ActorId!.Value).Distinct().ToList(), ct);
        var items = rows.Select(r => new AuditEntryDto(
            r.Id, r.ActorId, r.ActorId is { } a && people.TryGetValue(a, out var p) ? p.Name : null, r.ActorId is { } b && people.TryGetValue(b, out var q) ? q.Role : null,
            r.Action, r.TargetType, r.TargetId, ParseDetails(r.DetailsJson), full ? r.IpAddress : null, r.CreatedAt)).ToList();
        return new AuditPageDto(items, query.Page, query.PageSize, total);
    }

    /// <summary>Действия, которые есть в видимой части журнала, по алфавиту.</summary>
    public async Task<IReadOnlyList<string>> ListActionsAsync(Guid viewerId, bool superAdminRoute, CancellationToken ct)
    {
        var viewer = await RequireViewerAsync(viewerId, superAdminRoute, ct);
        return await _store.ListActionsAsync(viewer.Role == UserRole.SuperAdmin ? null : viewerId, ct);
    }

    private async Task<User> RequireViewerAsync(Guid viewerId, bool superAdminRoute, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(viewerId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        var allowed = superAdminRoute ? user.Role == UserRole.SuperAdmin : user.Role is UserRole.Admin or UserRole.SuperAdmin;
        if (!allowed || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException(superAdminRoute ? "superadmin_required" : "admin_required", "Нет доступа к журналу аудита.");
        }

        return user;
    }

    /// <summary>Время без указания пояса считаем UTC (так сказано в контракте); со смещением (в том числе привязанное к местному поясу сервера) приводим к UTC.</summary>
    private static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        var v => DateTime.SpecifyKind(v.Value, DateTimeKind.Utc),
    };

    private static JsonElement? ParseDetails(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;   // запись другого кода без разбираемых подробностей показываем без них
        }
    }
}
