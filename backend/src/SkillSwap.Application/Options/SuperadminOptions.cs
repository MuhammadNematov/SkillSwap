namespace SkillSwap.Application.Options;

/// <summary>
/// Первый суперадмин (раздел 3 ТЗ, правило 5): создаётся при развёртывании из переменных окружения
/// <c>Superadmin__Email</c> и <c>Superadmin__Password</c>, а не через публичную регистрацию.
/// Пароль — секрет: только переменная окружения или user-secrets, никогда не в репозитории.
/// </summary>
public sealed class SuperadminOptions
{
    public const string SectionName = "Superadmin";

    public string? Email { get; set; }

    public string? Password { get; set; }
}
