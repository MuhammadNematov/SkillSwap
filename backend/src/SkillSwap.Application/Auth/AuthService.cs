using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Auth;

public sealed class AuthService
{
    private readonly UserManager<User> _users;
    private readonly IMailSender _mail;
    private readonly IValidator<RegisterRequest> _registerValidator;
    private readonly IValidator<ConfirmEmailRequest> _confirmValidator;
    private readonly IValidator<ResendConfirmationRequest> _resendValidator;
    private readonly FrontendOptions _frontend;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<User> users,
        IMailSender mail,
        IValidator<RegisterRequest> registerValidator,
        IValidator<ConfirmEmailRequest> confirmValidator,
        IValidator<ResendConfirmationRequest> resendValidator,
        IOptions<FrontendOptions> frontend,
        ILogger<AuthService> logger)
    {
        _users = users;
        _mail = mail;
        _registerValidator = registerValidator;
        _confirmValidator = confirmValidator;
        _resendValidator = resendValidator;
        _frontend = frontend.Value;
        _logger = logger;
    }

    /// <param name="acceptLanguageHeader">Заголовок Accept-Language: используется, если язык в запросе не указан (FR-2).</param>
    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request, string? acceptLanguageHeader, CancellationToken ct = default)
    {
        await _registerValidator.ValidateAndThrowAsync(request, ct);

        var email = request.Email.Trim();
        var language = request.UiLanguage ?? SupportedLanguages.FromAcceptLanguage(acceptLanguageHeader);
        var user = User.CreateRegular(email, request.Name.Trim(), request.DateOfBirth, language);

        var result = await _users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            throw MapCreateError(result);
        }

        // Письмо не должно ломать регистрацию: если отправка упала, пользователь запросит его повторно.
        await TrySendConfirmationAsync(user, ct);

        return new RegisterResponse(user.Id, email);
    }

    public async Task ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken ct = default)
    {
        await _confirmValidator.ValidateAndThrowAsync(request, ct);

        var user = await _users.FindByIdAsync(request.UserId.ToString());
        // Одинаковый ответ на «нет такого пользователя» и «неверный токен» — не раскрываем, кто зарегистрирован.
        if (user is null)
        {
            throw InvalidConfirmationToken();
        }

        var result = await _users.ConfirmEmailAsync(user, request.Token);
        if (!result.Succeeded)
        {
            throw InvalidConfirmationToken();
        }
    }

    /// <summary>Всегда завершается без ошибки, даже если email неизвестен или уже подтверждён.</summary>
    public async Task ResendConfirmationAsync(ResendConfirmationRequest request, CancellationToken ct = default)
    {
        await _resendValidator.ValidateAndThrowAsync(request, ct);

        var user = await _users.FindByEmailAsync(request.Email.Trim());
        if (user is null || user.EmailConfirmed || user.Status != UserStatus.Active)
        {
            return;
        }

        await TrySendConfirmationAsync(user, ct);
    }

    private async Task TrySendConfirmationAsync(User user, CancellationToken ct)
    {
        try
        {
            var token = await _users.GenerateEmailConfirmationTokenAsync(user);
            var link = $"{_frontend.BaseUrl.TrimEnd('/')}/confirm-email" +
                       $"?userId={user.Id}&token={Uri.EscapeDataString(token)}";
            var (subject, body) = EmailTemplates.ConfirmEmail(user.UiLanguage, user.Name, link);
            await _mail.SendAsync(user.Email!, subject, body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось отправить письмо подтверждения пользователю {UserId}", user.Id);
        }
    }

    private static AppException MapCreateError(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail)
                                       or nameof(IdentityErrorDescriber.DuplicateUserName)))
        {
            return new ConflictException("email_already_registered", "Этот email уже зарегистрирован.");
        }

        var codes = string.Join(", ", result.Errors.Select(e => e.Code));
        return new BadRequestException("registration_failed", $"Не удалось создать пользователя: {codes}.");
    }

    private static BadRequestException InvalidConfirmationToken() =>
        new("invalid_confirmation_token", "Ссылка подтверждения недействительна или устарела.");
}
