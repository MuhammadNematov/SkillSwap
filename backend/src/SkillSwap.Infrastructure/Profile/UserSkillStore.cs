using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Profile;

public sealed class UserSkillStore : IUserSkillStore
{
    private readonly AppDbContext _db;

    public UserSkillStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<UserSkillRow>> ListAsync(Guid userId, CancellationToken ct) =>
        await Rows(_db.UserSkills.AsNoTracking().Where(s => s.UserId == userId)).ToListAsync(ct);

    public async Task<IReadOnlyList<UserSkillRow>> ListActiveAsync(Guid userId, CancellationToken ct) =>
        await Rows(_db.UserSkills.AsNoTracking().Where(s => s.UserId == userId && s.Skill!.IsActive && s.Skill.Category!.IsActive)).ToListAsync(ct);

    private static IQueryable<UserSkillRow> Rows(IQueryable<UserSkill> skills) => skills
        .OrderBy(s => s.Direction).ThenBy(s => s.Skill!.NameEn).ThenBy(s => s.SkillId)
        .Select(s => new UserSkillRow(
            s.Direction, s.SkillId, s.Skill!.CategoryId, s.Skill.NameTg, s.Skill.NameRu, s.Skill.NameEn,
            s.Level, s.Description, s.Skill.IsActive && s.Skill.Category!.IsActive));

    public async Task<HashSet<Guid>> FindActiveSkillIdsAsync(IReadOnlyCollection<Guid> skillIds, CancellationToken ct) =>
        (await _db.Skills.AsNoTracking()
            .Where(s => skillIds.Contains(s.Id) && s.IsActive && s.Category!.IsActive)
            .Select(s => s.Id)
            .ToListAsync(ct)).ToHashSet();

    public Task<bool> IsSkillsCompleteAsync(Guid userId, CancellationToken ct) => IsCompleteAsync(_db, userId, ct);

    /// <summary>Есть включённый навык и в «Умею», и в «Хочу изучать».</summary>
    internal static async Task<bool> IsCompleteAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        var directions = await db.UserSkills.AsNoTracking()
            .Where(s => s.UserId == userId && s.Skill!.IsActive && s.Skill.Category!.IsActive)
            .GroupBy(s => s.Direction)
            .Select(g => new { Direction = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return Has(directions.Select(d => (d.Direction, d.Count)), SkillDirection.Teach)
            && Has(directions.Select(d => (d.Direction, d.Count)), SkillDirection.Learn);

        static bool Has(IEnumerable<(SkillDirection Direction, int Count)> counts, SkillDirection direction) =>
            counts.Any(c => c.Direction == direction && c.Count >= UserSkill.MinPerDirectionForCompleteAnketa);
    }

    public async Task<SaveSkillsOutcome> ReplaceAsync(Guid userId, IReadOnlyList<DesiredSkill> desired, CancellationToken ct)
    {
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            // Та же блокировка, что и при правке профиля: анкета и «виден в подборе» меняются согласованно.
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE", ct);

            var existing = await _db.UserSkills.Where(s => s.UserId == userId).ToListAsync(ct);

            // Меняем то, что есть, добавляем новое, убираем лишнее: у оставшихся навыков сохраняются Id и дата добавления.
            foreach (var wish in desired)
            {
                var current = existing.FirstOrDefault(s => s.SkillId == wish.SkillId && s.Direction == wish.Direction);
                if (current is null)
                {
                    _db.UserSkills.Add(UserSkill.Create(userId, wish.SkillId, wish.Direction, wish.Level, wish.Description));
                }
                else
                {
                    current.Update(wish.Level, wish.Description);
                }
            }

            _db.UserSkills.RemoveRange(existing.Where(s => !desired.Any(w => w.SkillId == s.SkillId && w.Direction == s.Direction)));
            await _db.SaveChangesAsync(ct);

            var complete = await IsCompleteAsync(_db, userId, ct);
            var now = DateTime.UtcNow;
            if (!complete)
            {
                // Неполная анкета и «виден всем» несовместимы: скрываем пользователя сами (дата изменения тоже обновляется).
                // Это не решение пользователя, поэтому HiddenByUser не трогаем.
                await _db.Users.Where(u => u.Id == userId && u.IsSearchable)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsSearchable, false).SetProperty(x => x.UpdatedAt, now), ct);
            }
            else
            {
                // Анкета заполнена: показываем пользователя в подборе, если он сам не скрылся.
                // Скрывшегося вручную (HiddenByUser) сервер больше не включает: снова показаться он может только сам.
                await _db.Users.Where(u => u.Id == userId && !u.IsSearchable && !u.HiddenByUser)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsSearchable, true).SetProperty(x => x.UpdatedAt, now), ct);
            }

            var searchable = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.IsSearchable).SingleAsync(ct);
            await transaction.CommitAsync(ct);
            return new SaveSkillsOutcome(true, complete, searchable);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.UniqueViolation,
        })
        {
            // Навык удалили из каталога, пока шёл запрос, или двойная вставка: пусть клиент повторит.
            return new SaveSkillsOutcome(false, false, false);
        }
    }
}
