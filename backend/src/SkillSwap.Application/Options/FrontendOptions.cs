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

    public string RequestsUrl() => Combine(RequestsPath);

    public string ExchangeUrl(Guid exchangeId) => Combine(ExchangePath.Replace(IdPlaceholder, exchangeId.ToString(), StringComparison.Ordinal));

    private string Combine(string path) => BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}
