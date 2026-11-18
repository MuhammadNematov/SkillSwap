using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkillSwap.Application.Skills;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Skills;

public sealed class SkillSuggestionStore : ISkillSuggestionStore
{
    private readonly AppDbContext _db;

    public SkillSuggestionStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<SuggestionResult> CreateAsync(Guid userId, Guid categoryId, string cleanName, CancellationToken ct)
    {
        var normalized = SkillSuggestion.Normalize(cleanName);

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            // Блокировка строки пользователя: два одновременных запроса одного человека идут по очереди,
            // поэтому лимит неразобранных предложений и проверка повтора не обходятся гонкой.
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE", ct);

            var categoryOk = await _db.SkillCategories.AnyAsync(c => c.Id == categoryId && c.IsActive, ct);
            if (!categoryOk)
            {
                return new SuggestionResult(SuggestionOutcome.CategoryNotFound, null);
            }

            // Навык уже есть: совпало название на любом из трёх языков или синоним (они хранятся в нижнем регистре).
            // Возвращаем его id, чтобы фронтенд сразу предложил добавить существующий навык.
            var existingSkillId = await _db.Skills
                .Where(s => s.CategoryId == categoryId && s.IsActive &&
                    (s.NameTg.ToLower() == normalized || s.NameRu.ToLower() == normalized || s.NameEn.ToLower() == normalized ||
                     s.Synonyms.Contains(normalized)))
                .OrderBy(s => s.Id)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
            if (existingSkillId is not null)
            {
                return new SuggestionResult(SuggestionOutcome.AlreadyInCatalog, null, existingSkillId);
            }

            var pending = _db.SkillSuggestions.Where(s => s.UserId == userId && s.Status == SuggestionStatus.Pending);
            if (await pending.AnyAsync(s => s.CategoryId == categoryId && s.NormalizedName == normalized, ct))
            {
                return new SuggestionResult(SuggestionOutcome.AlreadyPending, null);
            }

            if (await pending.CountAsync(ct) >= SkillSuggestion.MaxPendingPerUser)
            {
                return new SuggestionResult(SuggestionOutcome.TooManyPending, null);
            }

            var suggestion = SkillSuggestion.Create(userId, categoryId, cleanName);
            _db.SkillSuggestions.Add(suggestion);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new SuggestionResult(SuggestionOutcome.Created, suggestion);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new SuggestionResult(SuggestionOutcome.AlreadyPending, null);   // запасной рубеж: уникальный индекс
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            return new SuggestionResult(SuggestionOutcome.CategoryNotFound, null);   // категорию удалили, пока шёл запрос
        }
    }
}
