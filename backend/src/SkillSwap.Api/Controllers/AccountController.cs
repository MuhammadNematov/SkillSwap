using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Account;

namespace SkillSwap.Api.Controllers;

/// <summary>Удаление аккаунта и выгрузка данных (раздел 8.1 ТЗ).</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
[Tags("Account")]
public class AccountController : ControllerBase
{
    private readonly AccountDeletionService _deletion;
    private readonly DataExportService _export;

    public AccountController(AccountDeletionService deletion, DataExportService export)
    {
        _deletion = deletion;
        _export = export;
    }

    /// <summary>
    /// Удалить аккаунт безвозвратно. Тело: <c>password</c> (обязателен) и <c>code</c> (код приложения или резервный код с <c>isRecoveryCode: true</c>;
    /// обязателен, если включена 2FA). Что происходит: активные обмены прерываются без виновного (партнёр видит нейтральную причину
    /// <c>partner_unavailable</c>), уроки отменяются, ожидающие заявки закрываются, блокировки и уведомления удаляются, личные данные (имя, о себе,
    /// дата рождения, языки, навыки, аватар) стираются, адрес почты заменяется обезличенным (его можно зарегистрировать заново), все сеансы отзываются.
    /// Сообщения остаются у партнёров до очистки через 90 дней, автор подписан «Удалённый пользователь» (<c>isDeleted: true</c>). На старый адрес
    /// уходит письмо. Если по пользователю открыто обращение, личные данные хранятся до его закрытия. Ошибки: 400 <c>invalid_password</c>,
    /// <c>two_factor_code_required</c>, <c>invalid_two_factor_code</c>, <c>validation_failed</c>; 403 <c>staff_cannot_delete_account</c>; 429 <c>account_locked_out</c>.
    /// </summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Delete([FromBody] DeleteAccountRequest request, CancellationToken ct)
    {
        await _deletion.DeleteAsync(UserId(), request, ct);
        RefreshCookie.Delete(Response);   // сеансы уже отозваны; убираем и cookie
        return NoContent();
    }

    /// <summary>
    /// Запросить выгрузку данных. Фоновая задача соберёт JSON (профиль, навыки, заявки, обмены, уроки, свои сообщения расшифрованными, блокировки,
    /// отзывы) и пришлёт на email ссылку на скачивание, она действует 24 часа; потом файл удаляется. Не чаще одного раза в сутки (429
    /// <c>export_rate_limited</c>; неудавшаяся выгрузка не мешает повторить). Ответ 202 с состоянием выгрузки, ссылки в нём нет.
    /// </summary>
    [HttpPost("export")]
    [ProducesResponseType<DataExportDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestExport(CancellationToken ct) =>
        Accepted(await _export.RequestAsync(UserId(), ct));

    /// <summary>Состояние последней выгрузки: Pending, Processing, Ready (с <c>expiresAt</c>), Failed, Expired. Не запрашивали — 404 <c>export_not_found</c>.</summary>
    [HttpGet("export")]
    [ProducesResponseType<DataExportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LastExport(CancellationToken ct) =>
        Ok(await _export.LastAsync(UserId(), ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
