using FluentValidation;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Skills;

/// <summary>
/// Правка каталога админом без изменения кода (раздел 5.2 ТЗ). Удалять можно только то, на что никто не ссылается;
/// остальное выключают (<c>isActive: false</c>): пользователи перестают видеть навык в каталоге, а чужие анкеты не ломаются.
/// </summary>
public sealed class SkillCatalogAdminService
{
    private readonly ISkillCatalogRepository _repository;
    private readonly IValidator<CategoryCreateRequest> _categoryCreate;
    private readonly IValidator<CategoryUpdateRequest> _categoryUpdate;
    private readonly IValidator<SkillCreateRequest> _skillCreate;
    private readonly IValidator<SkillUpdateRequest> _skillUpdate;

    public SkillCatalogAdminService(
        ISkillCatalogRepository repository,
        IValidator<CategoryCreateRequest> categoryCreate,
        IValidator<CategoryUpdateRequest> categoryUpdate,
        IValidator<SkillCreateRequest> skillCreate,
        IValidator<SkillUpdateRequest> skillUpdate)
    {
        _repository = repository;
        _categoryCreate = categoryCreate;
        _categoryUpdate = categoryUpdate;
        _skillCreate = skillCreate;
        _skillUpdate = skillUpdate;
    }

    // ---------- категории ----------

    public Task<IReadOnlyList<AdminCategoryDto>> ListCategoriesAsync(CancellationToken ct) =>
        _repository.ListCategoriesAsync(ct);

    public async Task<AdminCategoryDto> GetCategoryAsync(Guid id, CancellationToken ct)
    {
        var category = await RequireCategoryAsync(id, ct);
        return ToDto(category, await _repository.CountSkillsInCategoryAsync(id, ct));
    }

    public async Task<AdminCategoryDto> CreateCategoryAsync(CategoryCreateRequest request, CancellationToken ct)
    {
        await _categoryCreate.ValidateAndThrowAsync(request, ct);
        await EnsureCategoryNameFreeAsync(request.NameEn, null, ct);

        var category = SkillCategory.Create(request.NameTg, request.NameRu, request.NameEn, request.SortOrder);
        _repository.Add(category);
        await SaveAsync("category_name_taken", ct);
        return ToDto(category, 0);
    }

    public async Task<AdminCategoryDto> UpdateCategoryAsync(Guid id, CategoryUpdateRequest request, CancellationToken ct)
    {
        await _categoryUpdate.ValidateAndThrowAsync(request, ct);
        var category = await RequireCategoryAsync(id, ct);
        await EnsureCategoryNameFreeAsync(request.NameEn, id, ct);

        category.Update(request.NameTg, request.NameRu, request.NameEn, request.SortOrder, request.IsActive!.Value);
        await SaveAsync("category_name_taken", ct);
        return ToDto(category, await _repository.CountSkillsInCategoryAsync(id, ct));
    }

    public async Task DeleteCategoryAsync(Guid id, CancellationToken ct)
    {
        var category = await RequireCategoryAsync(id, ct);
        if (await _repository.CountSkillsInCategoryAsync(id, ct) > 0)
        {
            throw new ConflictException("category_not_empty",
                "В категории есть навыки. Перенесите их или выключите категорию (isActive: false).");
        }

        _repository.Remove(category);
        await SaveAsync("category_not_empty", ct);
    }

    // ---------- навыки ----------

    public Task<PagedResult<AdminSkillDto>> SearchSkillsAsync(
        Guid? categoryId, string? query, bool? isActive, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        var normalized = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (normalized is { Length: > SkillCatalogService.QueryMaxLength })
        {
            throw new BadRequestException("query_too_long", $"Поисковый запрос длиннее {SkillCatalogService.QueryMaxLength} символов.");
        }

        return _repository.SearchSkillsAsync(new AdminSkillSearch(categoryId, normalized, isActive, page, pageSize), ct);
    }

    public async Task<AdminSkillDto> GetSkillAsync(Guid id, CancellationToken ct) =>
        ToDto(await RequireSkillAsync(id, ct));

    public async Task<AdminSkillDto> CreateSkillAsync(SkillCreateRequest request, CancellationToken ct)
    {
        await _skillCreate.ValidateAndThrowAsync(request, ct);
        await RequireCategoryAsync(request.CategoryId, ct);
        await EnsureSkillNameFreeAsync(request.CategoryId, request.NameEn, null, ct);

        var skill = Skill.Create(request.CategoryId, request.NameTg, request.NameRu, request.NameEn, request.Synonyms);
        _repository.Add(skill);
        await SaveAsync("skill_name_taken", ct);
        return ToDto(skill);
    }

    public async Task<AdminSkillDto> UpdateSkillAsync(Guid id, SkillUpdateRequest request, CancellationToken ct)
    {
        await _skillUpdate.ValidateAndThrowAsync(request, ct);
        var skill = await RequireSkillAsync(id, ct);
        if (request.CategoryId != skill.CategoryId)
        {
            await RequireCategoryAsync(request.CategoryId, ct);
        }

        await EnsureSkillNameFreeAsync(request.CategoryId, request.NameEn, id, ct);

        skill.Update(request.CategoryId, request.NameTg, request.NameRu, request.NameEn, request.Synonyms, request.IsActive!.Value);
        await SaveAsync("skill_name_taken", ct);
        return ToDto(skill);
    }

    public async Task DeleteSkillAsync(Guid id, CancellationToken ct)
    {
        var skill = await RequireSkillAsync(id, ct);
        _repository.Remove(skill);
        await SaveAsync("skill_in_use", ct, "Навык указан в анкетах пользователей. Выключите его (isActive: false) вместо удаления.");
    }

    // ---------- общее ----------

    private async Task<SkillCategory> RequireCategoryAsync(Guid id, CancellationToken ct) =>
        await _repository.FindCategoryAsync(id, ct) ?? throw new NotFoundException("category_not_found", "Категория не найдена.");

    private async Task<Skill> RequireSkillAsync(Guid id, CancellationToken ct) =>
        await _repository.FindSkillAsync(id, ct) ?? throw new NotFoundException("skill_not_found", "Навык не найден.");

    private async Task EnsureCategoryNameFreeAsync(string nameEn, Guid? exceptId, CancellationToken ct)
    {
        if (await _repository.CategoryNameExistsAsync(nameEn.Trim(), exceptId, ct))
        {
            throw new ConflictException("category_name_taken", "Категория с таким английским названием уже есть.");
        }
    }

    private async Task EnsureSkillNameFreeAsync(Guid categoryId, string nameEn, Guid? exceptId, CancellationToken ct)
    {
        if (await _repository.SkillNameExistsAsync(categoryId, nameEn.Trim(), exceptId, ct))
        {
            throw new ConflictException("skill_name_taken", "Навык с таким английским названием уже есть в этой категории.");
        }
    }

    // Код при конфликте: «Duplicate» и «InUse» для одной операции используют один и тот же код ошибки.
    private async Task SaveAsync(string conflictCode, CancellationToken ct, string? inUseMessage = null)
    {
        switch (await _repository.TrySaveAsync(ct))
        {
            case CatalogSaveResult.Saved:
                return;
            case CatalogSaveResult.Duplicate:
                throw new ConflictException(conflictCode, "Такое название уже есть.");
            default:
                throw new ConflictException(conflictCode, inUseMessage ?? "Запись используется, изменить её нельзя.");
        }
    }

    private static AdminCategoryDto ToDto(SkillCategory c, int skillCount) =>
        new(c.Id, c.NameTg, c.NameRu, c.NameEn, c.SortOrder, c.IsActive, skillCount);

    private static AdminSkillDto ToDto(Skill s) =>
        new(s.Id, s.CategoryId, s.NameTg, s.NameRu, s.NameEn, s.Synonyms, s.IsActive);
}
