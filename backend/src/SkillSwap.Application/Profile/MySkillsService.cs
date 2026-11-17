using FluentValidation;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Profile;

/// <summary>Анкета: «Умею» и «Хочу изучать» (раздел 5.2 ТЗ). Подбор открывается после минимум 1 навыка в каждом направлении.</summary>
public sealed class MySkillsService
{
    private readonly IProfileStore _profiles;
    private readonly IUserSkillStore _skills;
    private readonly IValidator<SaveSkillsRequest> _validator;

    public MySkillsService(IProfileStore profiles, IUserSkillStore skills, IValidator<SaveSkillsRequest> validator)
    {
        _profiles = profiles;
        _skills = skills;
        _validator = validator;
    }

    public async Task<MySkillsDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("staff_has_no_profile", "У служебного аккаунта нет анкеты.");
        }

        return ToDto(await _skills.ListAsync(userId, ct), user.IsSearchable);
    }

    public async Task<MySkillsDto> SaveAsync(Guid userId, SaveSkillsRequest request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("staff_has_no_profile", "У служебного аккаунта нет анкеты.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }

        var desired = request.Teach!.Select(e => Desired(SkillDirection.Teach, e))
            .Concat(request.Learn!.Select(e => Desired(SkillDirection.Learn, e)))
            .ToList();

        var wanted = desired.Select(d => d.SkillId).Distinct().ToList();
        var known = await _skills.FindActiveSkillIdsAsync(wanted, ct);
        if (wanted.Any(id => !known.Contains(id)))
        {
            throw new BadRequestException("unknown_skill",
                "Некоторых навыков нет в каталоге или они выключены. Обновите каталог и уберите их из анкеты.");
        }

        var outcome = await _skills.ReplaceAsync(userId, desired, ct);
        if (!outcome.Saved)
        {
            throw new ConflictException("concurrency_conflict", "Анкета была изменена параллельным запросом. Повторите действие.");
        }

        return ToDto(await _skills.ListAsync(userId, ct), outcome.IsSearchable);
    }

    private static DesiredSkill Desired(SkillDirection direction, SkillEntryRequest e) =>
        new(direction, e.SkillId, e.Level!.Value, e.Description);

    private static MySkillsDto ToDto(IReadOnlyList<UserSkillRow> rows, bool isSearchable)
    {
        static UserSkillDto Map(UserSkillRow r) =>
            new(r.SkillId, r.CategoryId, r.NameTg, r.NameRu, r.NameEn, r.Level, r.Description, r.IsActive);

        var teach = rows.Where(r => r.Direction == SkillDirection.Teach).Select(Map).ToList();
        var learn = rows.Where(r => r.Direction == SkillDirection.Learn).Select(Map).ToList();
        var complete = teach.Any(s => s.IsActive) && learn.Any(s => s.IsActive);
        return new MySkillsDto(teach, learn, complete, isSearchable);
    }
}
