using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Blocks;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Блокировка пользователей (раздел 12 ТЗ). Блокировка действует в обе стороны: оба не видят друг друга в подборе и в профилях.
/// Заблокированный об этом не узнаёт: профиль заблокировавшего для него выглядит так же, как несуществующий.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[Tags("Profile")]
public class UserBlocksController : ControllerBase
{
    private readonly UserBlockService _blocks;

    public UserBlocksController(UserBlockService blocks)
    {
        _blocks = blocks;
    }

    /// <summary>
    /// Заблокировать пользователя. Повторный запрос не ошибка (204). В той же транзакции ожидающие заявки между вами (в любую сторону)
    /// становятся <c>Expired</c>, а активный обмен — <c>Aborted</c> без виновного (<c>abortedBy: null</c>). Причина в обмене: <c>blocked</c> у того, кто заблокировал, и нейтральная <c>partner_unavailable</c> у заблокированного (факт блокировки ему не раскрывается). Нельзя заблокировать себя (400 <c>cannot_block_self</c>), служебный
    /// или удалённый аккаунт и несуществующий id (404 <c>user_not_found</c>). Не больше 200 блокировок (409 <c>too_many_blocks</c>).
    /// Служебным аккаунтам — 403 <c>regular_account_required</c>.
    /// </summary>
    [HttpPost("users/{id:guid}/block")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Block(Guid id, CancellationToken ct)
    {
        await _blocks.BlockAsync(UserId(), id, ct);
        return NoContent();
    }

    /// <summary>Снять блокировку. Если её не было — тоже 204.</summary>
    [HttpDelete("users/{id:guid}/block")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Unblock(Guid id, CancellationToken ct)
    {
        await _blocks.UnblockAsync(UserId(), id, ct);
        return NoContent();
    }

    /// <summary>
    /// Мои заблокированные, новые сверху. Нужен, чтобы снять блокировку: профиль заблокированного через
    /// <c>GET /users/{id}</c> недоступен (404). Метод сверх списка из раздела 12 ТЗ.
    /// </summary>
    [HttpGet("me/blocks")]
    [ProducesResponseType<IReadOnlyList<BlockedUserDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _blocks.ListAsync(UserId(), ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
