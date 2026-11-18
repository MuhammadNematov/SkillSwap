using FluentValidation;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Skills;

/// <param name="Name">Название нового навыка (2–100 символов, хотя бы одна буква); на любом языке.</param>
public sealed record SuggestSkillRequest(Guid CategoryId, string Name);

public sealed record SkillSuggestionDto(Guid Id, Guid CategoryId, string Name, SuggestionStatus Status, DateTime CreatedAt);

public enum SuggestionOutcome
{
    Created,
    CategoryNotFound,

    /// <summary>В этой категории уже есть включённый навык с таким названием или синонимом.</summary>
    AlreadyInCatalog,

    /// <summary>Такое же предложение от этого пользователя уже ждёт разбора.</summary>
    AlreadyPending,

    TooManyPending,
}

public sealed record SuggestionResult(SuggestionOutcome Outcome, SkillSuggestion? Suggestion);

public interface ISkillSuggestionStore
{
    /// <summary>
    /// Проверяет и сохраняет предложение одной транзакцией под блокировкой строки пользователя: категория существует и включена,
    /// такого навыка ещё нет, нет такого же неразобранного предложения, лимит неразобранных не превышен.
    /// Под блокировкой, чтобы одновременные запросы не обошли лимит.
    /// </summary>
    Task<SuggestionResult> CreateAsync(Guid userId, Guid categoryId, string cleanName, CancellationToken ct);
}

public sealed class SuggestSkillRequestValidator : AbstractValidator<SuggestSkillRequest>
{
    public SuggestSkillRequestValidator()
    {
        RuleFor(x => x.CategoryId).NotEmpty().WithErrorCode("required");

        RuleFor(x => x.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithErrorCode("required")
            .DependentRules(() =>
            {
                RuleFor(x => x.Name)
                    .Must(n => Try(n, out _)).WithErrorCode("invalid_name");
            });
    }

    internal static bool Try(string? name, out string clean)
    {
        try
        {
            clean = SkillSuggestion.CleanName(name);
            return true;
        }
        catch (Domain.Exceptions.DomainValidationException)
        {
            clean = string.Empty;
            return false;
        }
    }
}

/// <summary>Предложение нового навыка пользователем (раздел 5.2 ТЗ). Разбирает админ на этапе 7.</summary>
public sealed class SkillSuggestionService
{
    private readonly IProfileStore _profiles;
    private readonly ISkillSuggestionStore _store;
    private readonly IValidator<SuggestSkillRequest> _validator;

    public SkillSuggestionService(IProfileStore profiles, ISkillSuggestionStore store, IValidator<SuggestSkillRequest> validator)
    {
        _profiles = profiles;
        _store = store;
        _validator = validator;
    }

    public async Task<SkillSuggestionDto> SuggestAsync(Guid userId, SuggestSkillRequest request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Предлагать навыки могут только обычные аккаунты.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }

        var result = await _store.CreateAsync(userId, request.CategoryId, SkillSuggestion.CleanName(request.Name), ct);
        return result.Outcome switch
        {
            SuggestionOutcome.Created => ToDto(result.Suggestion!),
            SuggestionOutcome.CategoryNotFound => throw new BadRequestException("unknown_category", "Категории нет или она выключена."),
            SuggestionOutcome.AlreadyInCatalog => throw new ConflictException("skill_already_exists",
                "Такой навык уже есть в этой категории: найдите его в каталоге."),
            SuggestionOutcome.AlreadyPending => throw new ConflictException("suggestion_already_pending",
                "Вы уже предложили этот навык, он ждёт решения администратора."),
            _ => throw new ConflictException("too_many_pending_suggestions",
                $"Одновременно может ждать разбора не больше {SkillSuggestion.MaxPendingPerUser} ваших предложений."),
        };
    }

    private static SkillSuggestionDto ToDto(SkillSuggestion s) => new(s.Id, s.CategoryId, s.SuggestedName, s.Status, s.CreatedAt);
}
