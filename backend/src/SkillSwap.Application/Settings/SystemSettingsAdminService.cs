using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Settings;

public sealed record SettingDto(string Key, string Value, string Description, SettingKind Kind, string Default, int? Min, int? Max, DateTime? UpdatedAt, bool IsDefault);

public sealed record SettingChange(string? Key, string? Value);

/// <param name="Settings">Новые значения (одна или несколько настроек за раз): применяются все или ни одной.</param>
/// <param name="Reason">Зачем меняем (необязательно, до 500 символов): пишется в аудит.</param>
public sealed record UpdateSettingsRequest(IReadOnlyList<SettingChange>? Settings, string? Reason, StepUpConfirmation? Confirmation);

/// <param name="Changed">Ключи, значения которых реально изменились (то же значение, что уже есть, не считается изменением).</param>
public sealed record UpdateSettingsResultDto(IReadOnlyList<SettingDto> Settings, IReadOnlyList<string> Changed);

/// <summary>Хранилище настроек для правки суперадмином.</summary>
public interface ISystemSettingsAdminStore
{
    /// <summary>Все строки таблицы с отслеживанием.</summary>
    Task<IReadOnlyList<SystemSetting>> ListAsync(CancellationToken ct);

    void Add(SystemSetting setting);

    Task SaveAsync(CancellationToken ct);
}

/// <summary>
/// Системные настройки суперадмина (раздел 3 ТЗ): чтение и изменение лимитов антиспама и возрастной политики подбора. Менять можно только настройки из реестра
/// (<see cref="SystemSettingDefinitions"/>), значение проверяется по типу и границам. Изменение требует пароль и код 2FA на каждый запрос, пишется в аудит (прежнее и новое значение
/// каждой настройки) и действует сразу: код читает настройки из базы при каждом использовании.
/// </summary>
public sealed class SystemSettingsAdminService
{
    public const int ReasonMaxLength = 500;

    private readonly ISystemSettingsAdminStore _store;
    private readonly IProfileStore _profiles;
    private readonly StepUpService _stepUp;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;

    public SystemSettingsAdminService(ISystemSettingsAdminStore store, IProfileStore profiles, StepUpService stepUp, IUnitOfWork uow, IAuditLog audit)
    {
        _store = store;
        _profiles = profiles;
        _stepUp = stepUp;
        _uow = uow;
        _audit = audit;
    }

    public async Task<IReadOnlyList<SettingDto>> ListAsync(Guid actorId, CancellationToken ct)
    {
        await RequireSuperAdminAsync(actorId, ct);
        return ToDtos(await _store.ListAsync(ct));
    }

    /// <summary>
    /// Изменить настройки. 400 <c>no_settings</c>, <c>unknown_setting</c>, <c>duplicate_setting</c>, <c>invalid_setting_value</c>, <c>reason_too_long</c> и ошибки подтверждения
    /// (<c>step_up_required</c>, <c>invalid_password</c>, ...). Подтверждение проверяется после остальных проверок, чтобы неверный запрос не сжигал код.
    /// </summary>
    public async Task<UpdateSettingsResultDto> UpdateAsync(Guid actorId, UpdateSettingsRequest request, CancellationToken ct)
    {
        await RequireSuperAdminAsync(actorId, ct);
        var changes = request.Settings ?? [];
        if (changes.Count == 0)
        {
            throw new BadRequestException("no_settings", "Не указано ни одной настройки.");
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > ReasonMaxLength })
        {
            throw new BadRequestException("reason_too_long", $"Причина не длиннее {ReasonMaxLength} символов.");
        }

        var wanted = new Dictionary<string, string>();
        foreach (var change in changes)
        {
            var definition = SystemSettingDefinitions.Find(change.Key)
                ?? throw new BadRequestException("unknown_setting", "Такой настройки нет.", new Dictionary<string, object?> { ["key"] = change.Key });
            var value = definition.Normalize(change.Value)
                ?? throw new BadRequestException("invalid_setting_value", "Значение не подходит.", new Dictionary<string, object?>
                {
                    ["key"] = definition.Key, ["kind"] = definition.Kind.ToString(), ["min"] = definition.Min, ["max"] = definition.Max,
                });
            if (!wanted.TryAdd(definition.Key, value))
            {
                throw new BadRequestException("duplicate_setting", "Настройка указана дважды.", new Dictionary<string, object?> { ["key"] = definition.Key });
            }
        }

        await _stepUp.RequireAsync(actorId, request.Confirmation, ct);

        var changed = new List<string>();
        await _uow.ExecuteAsync(async t =>
        {
            await _uow.LockUsersAsync([actorId], t);
            var rows = (await _store.ListAsync(t)).ToDictionary(r => r.Key);
            foreach (var (key, value) in wanted)
            {
                var definition = SystemSettingDefinitions.Find(key)!;
                var old = rows.TryGetValue(key, out var row) ? row.Value : null;
                if ((old ?? definition.Default) == value && row is not null)
                {
                    continue;   // то же значение: ничего не меняем и в аудит не пишем
                }

                if (row is null)
                {
                    _store.Add(SystemSetting.Create(key, value, definition.Description));
                }
                else
                {
                    row.ChangeValue(value);
                }

                changed.Add(key);
                _audit.Add(new AuditEntry(actorId, StaffAuditActions.SettingChanged, "SystemSetting", null, new { key, oldValue = old ?? definition.Default, newValue = value, reason }));
            }

            await _store.SaveAsync(t);
            return 0;
        }, ct);

        return new UpdateSettingsResultDto(ToDtos(await _store.ListAsync(ct)), changed);
    }

    private static IReadOnlyList<SettingDto> ToDtos(IReadOnlyList<SystemSetting> rows)
    {
        var byKey = rows.ToDictionary(r => r.Key);
        return SystemSettingDefinitions.All.Select(d =>
        {
            byKey.TryGetValue(d.Key, out var row);
            var value = row?.Value ?? d.Default;
            return new SettingDto(d.Key, value, d.Description, d.Kind, d.Default, d.Min, d.Max, row?.UpdatedAt, value == d.Default);
        }).ToList();
    }

    private async Task RequireSuperAdminAsync(Guid actorId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(actorId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role != UserRole.SuperAdmin || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("superadmin_required", "Доступно только суперадмину.");
        }
    }
}
