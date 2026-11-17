using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Infrastructure.Seeding;

/// <summary>
/// При старте приложения создаёт первого суперадмина из переменных окружения.
/// Идемпотентен: если суперадмин уже есть, ничего не делает и не трогает его пароль
/// (смена пароля в окружении не должна молча перезаписывать учётную запись).
/// Создан без 2FA: при первом входе система потребует её настроить (служебным 2FA обязательна).
/// </summary>
public sealed class SuperadminSeeder : IHostedService
{
    public const string DefaultName = "Superadmin";

    private readonly IServiceScopeFactory _scopes;
    private readonly SuperadminOptions _options;
    private readonly ILogger<SuperadminSeeder> _logger;

    public SuperadminSeeder(
        IServiceScopeFactory scopes,
        IOptions<SuperadminOptions> options,
        ILogger<SuperadminSeeder> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var emailGiven = !string.IsNullOrWhiteSpace(_options.Email);
        var passwordGiven = !string.IsNullOrWhiteSpace(_options.Password);

        if (!emailGiven && !passwordGiven)
        {
            _logger.LogInformation("Superadmin__Email и Superadmin__Password не заданы: суперадмин не создаётся.");
            return;
        }

        if (emailGiven != passwordGiven)
        {
            // Половина настройки — почти наверняка ошибка развёртывания. Лучше упасть сразу, чем тихо не создать.
            throw new InvalidOperationException(
                "Заданы не обе переменные: нужны и Superadmin__Email, и Superadmin__Password.");
        }

        using var scope = _scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        if (await users.Users.AnyAsync(u => u.Role == UserRole.SuperAdmin, cancellationToken))
        {
            _logger.LogInformation("Суперадмин уже существует: ничего не создаём.");
            return;
        }

        var email = _options.Email!.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            throw new InvalidOperationException(
                $"Нельзя создать суперадмина: email {email} уже занят другой учётной записью.");
        }

        var user = User.CreateStaff(email, DefaultName, UserRole.SuperAdmin);
        user.EmailConfirmed = true;   // создаётся администратором при развёртывании, письмо не нужно

        var result = await users.CreateAsync(user, _options.Password!);
        if (!result.Succeeded)
        {
            if (await users.Users.AnyAsync(u => u.Role == UserRole.SuperAdmin, cancellationToken))
            {
                return;   // параллельный запуск другой копии приложения успел раньше
            }

            // Пароль в сообщение не попадает, только коды ошибок Identity (например, PasswordTooShort).
            throw new InvalidOperationException(
                "Не удалось создать суперадмина: " + string.Join(", ", result.Errors.Select(e => e.Code)) + ".");
        }

        _logger.LogWarning(
            "Создан суперадмин {Email}. При первом входе система потребует настроить 2FA.", email);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
