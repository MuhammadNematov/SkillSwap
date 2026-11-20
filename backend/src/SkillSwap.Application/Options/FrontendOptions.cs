namespace SkillSwap.Application.Options;

/// <summary>Настройки фронтенда: адрес и маршруты нужны для ссылок в письмах. Секция "Frontend".</summary>
public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";

    /// <summary>Подстановка в <see cref="ExchangePath"/>: id обмена.</summary>
    public const string IdPlaceholder = "{id}";

    public string BaseUrl { get; set; } = "http://localhost:5173";

    /// <summary>Страница заявок (ссылка в письме «новая заявка»). Меняется настройкой без правки кода: <c>Frontend__RequestsPath</c>.</summary>
    public string RequestsPath { get; set; } = "/requests";

    /// <summary>Страница обмена (ссылка в письме «заявка принята»); <c>{id}</c> заменяется на id обмена: <c>Frontend__ExchangePath</c>.</summary>
    public string ExchangePath { get; set; } = "/exchanges/{id}";

    /// <summary>Страница обращения (ссылка в письме о решении); <c>{id}</c> заменяется на id обращения: <c>Frontend__CasePath</c>.</summary>
    public string CasePath { get; set; } = "/cases/{id}";

    /// <summary>Страница приёма приглашения служебного аккаунта (ссылка в письме); <c>{token}</c> заменяется на токен приглашения: <c>Frontend__StaffInvitationPath</c>.</summary>
    public string StaffInvitationPath { get; set; } = "/staff/invitation/{token}";

    public string StaffInvitationUrl(string token) => Combine(StaffInvitationPath.Replace("{token}", Uri.EscapeDataString(token), StringComparison.Ordinal));

    /// <summary>Страница подтверждения привязки личного аккаунта к служебному (ссылка в письме); <c>{token}</c> заменяется на токен запроса: <c>Frontend__PersonalLinkPath</c>.</summary>
    public string PersonalLinkPath { get; set; } = "/staff-link/{token}";

    public string PersonalLinkUrl(string token) => Combine(PersonalLinkPath.Replace("{token}", Uri.EscapeDataString(token), StringComparison.Ordinal));

    public string CaseUrl(Guid caseId) => Combine(CasePath.Replace(IdPlaceholder, caseId.ToString(), StringComparison.Ordinal));

    public string RequestsUrl() => Combine(RequestsPath);

    public string ExchangeUrl(Guid exchangeId) => Combine(ExchangePath.Replace(IdPlaceholder, exchangeId.ToString(), StringComparison.Ordinal));

    private string Combine(string path) => BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}
