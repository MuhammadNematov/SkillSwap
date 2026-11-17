using SkillSwap.Application.Exceptions;

namespace SkillSwap.Application.Common;

/// <summary>Страница списка: <c>page</c> с единицы, <c>pageSize</c> не больше 50 (раздел 12 ТЗ).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    /// <summary>Лишнее не обрезается молча: клиент, запросивший 500 записей, должен узнать об ошибке.</summary>
    public static void Validate(int page, int pageSize)
    {
        if (page < 1)
        {
            throw new BadRequestException("invalid_paging", "page должен быть не меньше 1.");
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new BadRequestException("invalid_paging", $"pageSize должен быть от 1 до {MaxPageSize}.");
        }
    }
}
