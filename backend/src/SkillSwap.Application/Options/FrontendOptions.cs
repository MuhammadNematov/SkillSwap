namespace SkillSwap.Application.Options;

/// <summary>Настройки фронтенда: адрес нужен для ссылок в письмах. Секция "Frontend".</summary>
public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";

    public string BaseUrl { get; set; } = "http://localhost:5173";
}
