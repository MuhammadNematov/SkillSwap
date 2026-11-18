using FluentValidation;
using SkillSwap.Application.Blocks;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Profile;

/// <summary>Профиль: свой (чтение и правка) и чужой (только чтение), разделы 5.2 и 8.1 ТЗ.</summary>
public sealed class ProfileService
{
    private readonly IProfileStore _store;
    private readonly IUserSkillStore _skills;
    private readonly IUserBlockStore _blocks;
    private readonly IValidator<UpdateProfileRequest> _validator;
    private readonly TimeProvider _clock;

    public ProfileService(
        IProfileStore store, IUserSkillStore skills, IUserBlockStore blocks, IValidator<UpdateProfileRequest> validator, TimeProvider clock)
    {
        _store = store;
        _skills = skills;
        _blocks = blocks;
        _validator = validator;
        _clock = clock;
    }

    public async Task<MyProfileDto> GetMineAsync(Guid userId, CancellationToken ct)
    {
        var user = await _store.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        return ToMine(user, await _store.GetLanguagesAsync(userId, ct), await _skills.IsSkillsCompleteAsync(userId, ct));
    }

    public async Task<MyProfileDto> UpdateMineAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var user = await _store.FindUserForUpdateAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("staff_has_no_profile", "У служебного аккаунта нет профиля.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }

        var languages = request.Languages!.Select(LessonLanguages.Normalize).Distinct().Order(StringComparer.Ordinal).ToArray();
        user.UpdateProfile(request.Name, request.Bio, request.UiLanguage, request.TimeZone, request.IsSearchable!.Value);

        switch (await _store.SaveProfileAsync(user, languages, ct))
        {
            case ProfileSaveResult.Conflict:
                throw new ConflictException("concurrency_conflict", "Профиль был изменён параллельным запросом. Повторите действие.");
            case ProfileSaveResult.SkillsIncomplete:
                throw new ForbiddenException("skills_incomplete",
                    "Чтобы показываться в подборе, заполните анкету: минимум 1 навык в «Умею» и 1 в «Хочу изучать».");
        }

        return ToMine(user, languages, await _skills.IsSkillsCompleteAsync(userId, ct));
    }

    /// <summary>
    /// Профиль другого пользователя. Нельзя увидеть: служебные аккаунты, заблокированных и удалённых, а также скрытых
    /// из подбора (кроме самого себя). Во всех этих случаях ответ одинаков: 404, чтобы по нему нельзя было узнать,
    /// почему профиля нет. Так же скрыт профиль при блокировке в любую сторону. Возрастные группы добавятся на шаге 6.
    /// </summary>
    public async Task<PublicProfileDto> GetPublicAsync(Guid viewerId, bool viewerIsRegular, Guid targetId, CancellationToken ct)
    {
        if (!viewerIsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Профили пользователей доступны только обычным аккаунтам.");
        }

        var user = await _store.FindUserAsync(targetId, ct);
        if (user is null || !user.IsRegular || user.Status != UserStatus.Active || (user.Id != viewerId && !user.IsSearchable))
        {
            throw new NotFoundException("user_not_found", "Пользователь не найден.");
        }

        // Блокировка в любую сторону: профиль для этой пары не существует. Ответ тот же 404, что и для скрытого или удалённого,
        // поэтому заблокированный не узнаёт, что его заблокировали.
        if (user.Id != viewerId && await _blocks.IsBlockedEitherWayAsync(viewerId, user.Id, ct))
        {
            throw new NotFoundException("user_not_found", "Пользователь не найден.");
        }

        var languages = await _store.GetLanguagesAsync(targetId, ct);
        var skills = await _skills.ListActiveAsync(targetId, ct);

        static PublicSkillDto Map(UserSkillRow r) => new(r.SkillId, r.CategoryId, r.NameTg, r.NameRu, r.NameEn, r.Level, r.Description);

        return new PublicProfileDto(
            user.Id, user.Name, user.Bio, user.AvatarUrl, user.TimeZone, languages,
            skills.Where(s => s.Direction == SkillDirection.Teach).Select(Map).ToList(),
            skills.Where(s => s.Direction == SkillDirection.Learn).Select(Map).ToList(),
            user.IsMinor(Today()), PublicRating(user), user.RatingCount, user.IsNewcomer, user.CreatedAt);
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    /// <summary>У «Новичка» средней оценки нет: два отзыва ничего не говорят о человеке.</summary>
    private static decimal? PublicRating(User user) => user.IsNewcomer ? null : user.RatingAvg;

    private static MyProfileDto ToMine(User user, string[] languages, bool skillsComplete) => new(
        user.Id, user.Email ?? string.Empty, user.Role.ToString(), user.AccountType.ToString(),
        user.Name, user.Bio, user.AvatarUrl, user.DateOfBirth, user.UiLanguage, user.TimeZone, user.IsSearchable,
        languages, skillsComplete, PublicRating(user), user.RatingCount, user.IsNewcomer, user.TwoFactorEnabled, user.CreatedAt);
}
