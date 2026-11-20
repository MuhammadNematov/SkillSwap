using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Skills;

/// <summary>Навык каталога, похожий на предложение (поможет объединить, а не заводить дубль).</summary>
public sealed record SimilarSkillDto(Guid Id, string NameTg, string NameRu, string NameEn, bool IsActive);

/// <param name="SameNameCount">Сколько неразобранных предложений с таким же названием в этой категории (с этим). Они разбираются вместе с этим.</param>
/// <param name="ResolvedAt">Когда разобрано (null у неразобранных).</param>
public sealed record AdminSuggestionDto(
    Guid Id, Guid UserId, string UserName, Guid CategoryId, string CategoryNameEn, string Name, SuggestionStatus Status, DateTime CreatedAt, DateTime? ResolvedAt,
    Guid? ResolvedSkillId, Guid? ResolvedById, int SameNameCount, IReadOnlyList<SimilarSkillDto> Similar);

public sealed record AdminSuggestionListDto(IReadOnlyList<AdminSuggestionDto> Items, int Page, int PageSize, int TotalCount);

/// <param name="NameTg">Названия навыка на трёх языках: все обязательны (фронтенд подставляет предложенное название и просит дописать остальные).</param>
/// <param name="CategoryId">Другая категория вместо предложенной (необязательно).</param>
public sealed record ApproveSuggestionRequest(string? NameTg, string? NameRu, string? NameEn, string[]? Synonyms, Guid? CategoryId);

/// <param name="SkillId">Существующий навык, с которым объединяем.</param>
/// <param name="AddAsSynonym">Добавить предложенное название синонимом навыка для поиска (по умолчанию да).</param>
public sealed record MergeSuggestionRequest(Guid SkillId, bool? AddAsSynonym);

/// <param name="Reason">Причина отказа, необязательна, до 500 символов: её увидит автор.</param>
public sealed record RejectSuggestionRequest(string? Reason);

public sealed record SuggestionRow(SkillSuggestion Suggestion, string UserName, string CategoryNameEn);

/// <summary>Выборки для рассмотрения предложений навыков админом.</summary>
public interface ISkillSuggestionAdminStore
{
    Task<(IReadOnlyList<SuggestionRow> Items, int Total)> SearchAsync(SuggestionStatus status, Guid? categoryId, int page, int pageSize, CancellationToken ct);

    /// <summary>Предложение для изменения (с отслеживанием).</summary>
    Task<SkillSuggestion?> FindAsync(Guid id, bool tracked, CancellationToken ct);

    /// <summary>Другие неразобранные предложения с таким же названием в категории (с отслеживанием).</summary>
    Task<IReadOnlyList<SkillSuggestion>> FindPendingSameAsync(Guid categoryId, string normalizedName, Guid exceptId, CancellationToken ct);

    /// <summary>Сколько неразобранных предложений с каждым из пар (категория, название).</summary>
    Task<IReadOnlyDictionary<(Guid CategoryId, string Name), int>> CountPendingSameAsync(IReadOnlyCollection<(Guid CategoryId, string Name)> keys, CancellationToken ct);

    /// <summary>Навыки категории, в названии или синонимах которых встречается текст (до <paramref name="limit"/> штук).</summary>
    Task<IReadOnlyList<Skill>> FindSimilarSkillsAsync(Guid categoryId, string normalizedName, int limit, CancellationToken ct);

    Task<string?> GetUserNameAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Рассмотрение предложений навыков админом (раздел 5.2 ТЗ): одобрить (создаётся навык), объединить с существующим или отклонить. Автор получает уведомление. Остальные неразобранные
/// предложения с тем же названием в той же категории разбираются вместе с этим как объединённые с тем же навыком, их авторы тоже получают уведомление.
/// Доступно админу и суперадмину (статус проверяется по базе, а не по токену).
/// </summary>
public sealed class SkillSuggestionAdminService
{
    public const int SimilarLimit = 5;

    private readonly ISkillSuggestionAdminStore _store;
    private readonly ISkillCatalogRepository _catalog;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly TimeProvider _clock;

    public SkillSuggestionAdminService(
        ISkillSuggestionAdminStore store, ISkillCatalogRepository catalog, IProfileStore profiles, IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify, TimeProvider clock)
    {
        _store = store;
        _catalog = catalog;
        _profiles = profiles;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _clock = clock;
    }

    /// <summary>Очередь предложений: по умолчанию неразобранные (старые сверху), или с фильтром по статусу и категории.</summary>
    public async Task<AdminSuggestionListDto> ListAsync(Guid adminId, SuggestionStatus? status, Guid? categoryId, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        await RequireAdminAsync(adminId, ct);
        var (rows, total) = await _store.SearchAsync(status ?? SuggestionStatus.Pending, categoryId, page, pageSize, ct);
        var pending = rows.Where(r => r.Suggestion.Status == SuggestionStatus.Pending).ToList();
        var counts = await _store.CountPendingSameAsync(pending.Select(r => (r.Suggestion.CategoryId, r.Suggestion.NormalizedName)).Distinct().ToList(), ct);
        var items = new List<AdminSuggestionDto>();
        foreach (var row in rows)
        {
            var s = row.Suggestion;
            var isPending = s.Status == SuggestionStatus.Pending;
            var similar = isPending
                ? (await _store.FindSimilarSkillsAsync(s.CategoryId, s.NormalizedName, SimilarLimit, ct)).Select(k => new SimilarSkillDto(k.Id, k.NameTg, k.NameRu, k.NameEn, k.IsActive)).ToList()
                : new List<SimilarSkillDto>();
            items.Add(new AdminSuggestionDto(
                s.Id, s.UserId, row.UserName, s.CategoryId, row.CategoryNameEn, s.SuggestedName, s.Status, s.CreatedAt, isPending ? null : s.UpdatedAt, s.ResolvedSkillId, s.ResolvedById,
                isPending ? counts.GetValueOrDefault((s.CategoryId, s.NormalizedName), 1) : 0, similar));
        }

        return new AdminSuggestionListDto(items, page, pageSize, total);
    }

    /// <summary>
    /// Одобрить: создаётся навык (названия на трёх языках обязательны). 400 <c>name_required</c>/<c>name_too_long</c>, ошибки синонимов; 404 <c>suggestion_not_found</c>,
    /// <c>category_not_found</c>; 409 <c>skill_name_taken</c> (такой навык уже есть: объедините), <c>invalid_state_transition</c> (уже разобрано).
    /// </summary>
    public async Task<AdminSuggestionDto> ApproveAsync(Guid adminId, Guid id, ApproveSuggestionRequest request, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var peek = await _store.FindAsync(id, tracked: false, ct) ?? throw new NotFoundException("suggestion_not_found", "Предложение не найдено.");
        var categoryId = request.CategoryId ?? peek.CategoryId;

        await _uow.ExecuteAsync(async t =>
        {
            await _uow.LockUsersAsync([adminId, peek.UserId], t);   // решения по предложениям одного автора идут по очереди
            var found = await _store.FindAsync(id, tracked: true, t) ?? throw new NotFoundException("suggestion_not_found", "Предложение не найдено.");
            if (found.Status != SuggestionStatus.Pending)
            {
                throw new ConflictException("invalid_state_transition", "Предложение уже разобрано.");
            }

            _ = await _catalog.FindCategoryAsync(categoryId, t) ?? throw new NotFoundException("category_not_found", "Категория не найдена.");
            var nameEn = request.NameEn?.Trim() ?? string.Empty;
            if (nameEn.Length > 0 && await _catalog.SkillNameExistsAsync(categoryId, nameEn, null, t))
            {
                throw new ConflictException("skill_name_taken", "Навык с таким английским названием уже есть в этой категории: объедините предложение с ним.");
            }

            var skill = Skill.Create(categoryId, request.NameTg ?? string.Empty, request.NameRu ?? string.Empty, request.NameEn ?? string.Empty, request.Synonyms);
            _catalog.Add(skill);
            found.Approve(adminId, skill.Id);
            var merged = await MergeDuplicatesAsync(adminId, found, skill.Id, t);
            _audit.Add(new AuditEntry(adminId, StaffAuditActions.SkillSuggestionApproved, "SkillSuggestion", found.Id, new { skillId = skill.Id, categoryId, name = found.SuggestedName, mergedDuplicates = merged }));
            _notify.Add(found.UserId, NotificationType.SkillSuggestionApproved, new { suggestionId = found.Id, name = found.SuggestedName, skillId = skill.Id });
            await SaveAsync("skill_name_taken", t);
            return 0;
        }, ct);

        _notify.Dispatch();
        return await OneAsync(id, ct);
    }

    /// <summary>
    /// Объединить с существующим навыком: по умолчанию предложенное название добавляется ему синонимом (поиск находит). 404 <c>suggestion_not_found</c>, <c>skill_not_found</c>;
    /// 409 <c>skill_not_active</c> (выключенный навык), <c>invalid_state_transition</c>.
    /// </summary>
    public async Task<AdminSuggestionDto> MergeAsync(Guid adminId, Guid id, MergeSuggestionRequest request, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var peek = await _store.FindAsync(id, tracked: false, ct) ?? throw new NotFoundException("suggestion_not_found", "Предложение не найдено.");

        await _uow.ExecuteAsync(async t =>
        {
            await _uow.LockUsersAsync([adminId, peek.UserId], t);
            var found = await _store.FindAsync(id, tracked: true, t) ?? throw new NotFoundException("suggestion_not_found", "Предложение не найдено.");
            if (found.Status != SuggestionStatus.Pending)
            {
                throw new ConflictException("invalid_state_transition", "Предложение уже разобрано.");
            }

            var skill = await _catalog.FindSkillAsync(request.SkillId, t) ?? throw new NotFoundException("skill_not_found", "Навык не найден.");
            if (!skill.IsActive)
            {
                throw new ConflictException("skill_not_active", "Навык выключен: включите его в каталоге или выберите другой.");
            }

            found.Merge(adminId, skill.Id);
            var synonymAdded = (request.AddAsSynonym ?? true) && skill.AddSynonym(found.NormalizedName);
            var merged = await MergeDuplicatesAsync(adminId, found, skill.Id, t);
            _audit.Add(new AuditEntry(adminId, StaffAuditActions.SkillSuggestionMerged, "SkillSuggestion", found.Id, new { skillId = skill.Id, name = found.SuggestedName, synonymAdded, mergedDuplicates = merged }));
            _notify.Add(found.UserId, NotificationType.SkillSuggestionMerged, new { suggestionId = found.Id, name = found.SuggestedName, skillId = skill.Id });
            await SaveAsync("skill_name_taken", t);
            return 0;
        }, ct);

        _notify.Dispatch();
        return await OneAsync(id, ct);
    }

    /// <summary>Отклонить; причина необязательна (до 500 символов), её увидит автор. 400 <c>reject_reason_too_long</c>; 404 <c>suggestion_not_found</c>; 409 <c>invalid_state_transition</c>.</summary>
    public async Task<AdminSuggestionDto> RejectAsync(Guid adminId, Guid id, RejectSuggestionRequest? request, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > SkillSuggestion.RejectReasonMaxLength })
        {
            throw new BadRequestException("reject_reason_too_long", $"Причина не длиннее {SkillSuggestion.RejectReasonMaxLength} символов.");
        }

        var peek = await _store.FindAsync(id, tracked: false, ct) ?? throw new NotFoundException("suggestion_not_found", "Предложение не найдено.");
        await _uow.ExecuteAsync(async t =>
        {
            await _uow.LockUsersAsync([adminId, peek.UserId], t);
            var found = await _store.FindAsync(id, tracked: true, t) ?? throw new NotFoundException("suggestion_not_found", "Предложение не найдено.");
            if (found.Status != SuggestionStatus.Pending)
            {
                throw new ConflictException("invalid_state_transition", "Предложение уже разобрано.");
            }

            found.Reject(adminId);
            _audit.Add(new AuditEntry(adminId, StaffAuditActions.SkillSuggestionRejected, "SkillSuggestion", found.Id, new { name = found.SuggestedName, reason }));
            _notify.Add(found.UserId, NotificationType.SkillSuggestionRejected, new { suggestionId = found.Id, name = found.SuggestedName, reason });
            await SaveAsync("skill_name_taken", t);
            return 0;
        }, ct);

        _notify.Dispatch();
        return await OneAsync(id, ct);
    }

    // ---------- внутреннее ----------

    /// <summary>Остальные неразобранные предложения с тем же названием в той же категории объединяются с тем же навыком; их авторам уходит уведомление.</summary>
    private async Task<int> MergeDuplicatesAsync(Guid adminId, SkillSuggestion found, Guid skillId, CancellationToken ct)
    {
        var duplicates = await _store.FindPendingSameAsync(found.CategoryId, found.NormalizedName, found.Id, ct);
        foreach (var duplicate in duplicates)
        {
            duplicate.Merge(adminId, skillId);
            _audit.Add(new AuditEntry(adminId, StaffAuditActions.SkillSuggestionMerged, "SkillSuggestion", duplicate.Id, new { skillId, name = duplicate.SuggestedName, duplicateOf = found.Id }));
            _notify.Add(duplicate.UserId, NotificationType.SkillSuggestionMerged, new { suggestionId = duplicate.Id, name = duplicate.SuggestedName, skillId });
        }

        return duplicates.Count;
    }

    private async Task SaveAsync(string conflictCode, CancellationToken ct)
    {
        if (await _catalog.TrySaveAsync(ct) != CatalogSaveResult.Saved)
        {
            throw new ConflictException(conflictCode, "Такое название уже есть: объедините предложение с существующим навыком.");
        }
    }

    private async Task<AdminSuggestionDto> OneAsync(Guid id, CancellationToken ct)
    {
        var s = (await _store.FindAsync(id, tracked: false, ct))!;
        var skillIds = s.ResolvedSkillId;
        var userName = await _store.GetUserNameAsync(s.UserId, ct) ?? "—";
        var category = await _catalog.FindCategoryAsync(s.CategoryId, ct);
        return new AdminSuggestionDto(
            s.Id, s.UserId, userName, s.CategoryId, category?.NameEn ?? "—", s.SuggestedName, s.Status, s.CreatedAt, s.Status == SuggestionStatus.Pending ? null : s.UpdatedAt, skillIds, s.ResolvedById, 0, []);
    }

    private async Task RequireAdminAsync(Guid adminId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(adminId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role is not (UserRole.Admin or UserRole.SuperAdmin) || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("admin_required", "Доступно только администратору.");
        }
    }
}
