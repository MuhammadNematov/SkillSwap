using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Profile;

public sealed class ProfileStore : IProfileStore
{
    private readonly AppDbContext _db;

    public ProfileStore(AppDbContext db)
    {
        _db = db;
    }

    public Task<User?> FindUserAsync(Guid id, CancellationToken ct) =>
        _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<IReadOnlyDictionary<Guid, User>> FindUsersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? new Dictionary<Guid, User>() : await _db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

    public async Task<IReadOnlyDictionary<Guid, DateOnly>> GetBirthDatesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id) && u.DateOfBirth != null)
            .ToDictionaryAsync(u => u.Id, u => u.DateOfBirth!.Value, ct);

    public async Task SetAvatarKeyAsync(Guid userId, string? avatarKey, CancellationToken ct) =>
        await _db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.AvatarKey, avatarKey), ct);

    public Task<User?> FindUserForUpdateAsync(Guid id, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<string[]> GetLanguagesAsync(Guid userId, CancellationToken ct) =>
        _db.UserLanguages.AsNoTracking()
            .Where(l => l.UserId == userId)
            .OrderBy(l => l.LanguageCode)
            .Select(l => l.LanguageCode)
            .ToArrayAsync(ct);

    public async Task<ProfileSaveResult> SaveProfileAsync(User user, IReadOnlyCollection<string> languages, CancellationToken ct)
    {
        try
        {
            // Одна транзакция: профиль и языки меняются вместе или не меняются совсем.
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            // Блокируем строку пользователя: одновременные правки одного профиля (и анкеты) идут по очереди. Иначе два запроса
            // с разными наборами языков увидели бы одно и то же «было» и сложили бы оба набора вместе.
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {user.Id} FOR UPDATE", ct);

            // Полна ли анкета до этой правки (навыки и языки так, как они лежат в БД под блокировкой).
            var wasComplete = await UserSkillStore.IsCompleteAsync(_db, user.Id, ct);
            var wasVisible = _db.Entry(user).Property(u => u.IsSearchable).OriginalValue;

            var existing = await _db.UserLanguages.Where(l => l.UserId == user.Id).ToListAsync(ct);
            _db.UserLanguages.RemoveRange(existing.Where(l => !languages.Contains(l.LanguageCode)));
            foreach (var code in languages.Where(c => existing.All(l => l.LanguageCode != c)))
            {
                _db.UserLanguages.Add(UserLanguage.Create(user.Id, code));
            }

            await _db.SaveChangesAsync(ct);

            // Видимость и полнота анкеты считаются уже по новым языкам. Правила те же, что и при правке навыков:
            //  - просят показаться, а анкета неполная: отказ (ничего не сохраняется);
            //  - был виден, а анкета перестала быть полной (например, убрали все языки): скрываем сами, это не отказ;
            //  - анкета стала полной (например, добавили первый язык) и человек сам не скрывался: показываем сами.
            var isComplete = await UserSkillStore.IsCompleteAsync(_db, user.Id, ct);
            if (user.IsSearchable && !isComplete)
            {
                if (!wasVisible)
                {
                    return ProfileSaveResult.SkillsIncomplete;   // транзакция откатится при выходе
                }

                user.IsSearchable = false;
                await _db.SaveChangesAsync(ct);
            }
            else if (!user.IsSearchable && isComplete && !wasComplete && !user.HiddenByUser)
            {
                user.IsSearchable = true;
                await _db.SaveChangesAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return ProfileSaveResult.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ProfileSaveResult.Conflict;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Два запроса одновременно добавили один и тот же язык.
            return ProfileSaveResult.Conflict;
        }
    }
}
