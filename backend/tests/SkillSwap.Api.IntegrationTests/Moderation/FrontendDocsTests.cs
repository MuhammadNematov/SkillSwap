using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>
/// Сводка для фронтенда (<c>docs/FRONTEND-CASES.md</c>) не должна отставать от кода: каждый тип уведомления этапа 6, каждый статус, решение, тип и категория обращения
/// в ней упомянуты. Добавили значение и не описали его, тест падает.
/// </summary>
public class FrontendDocsTests
{
    private static readonly string Doc = File.ReadAllText(DocPath());

    private static string DocPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs")) && Directory.Exists(Path.Combine(dir.FullName, "backend")))
            {
                return Path.Combine(dir.FullName, "docs", "FRONTEND-CASES.md");
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория (каталоги docs и backend).");
    }

    [Fact]
    public void Every_notification_type_of_stage_six_is_described()
    {
        var all = Enum.GetValues<NotificationType>().ToList();
        var stageSix = all.Skip(all.IndexOf(NotificationType.ReviewAvailable)).ToList();

        var missing = stageSix.Where(t => !Doc.Contains($"`{t}`")).ToList();

        Assert.True(missing.Count == 0, "Не описаны в docs/FRONTEND-CASES.md: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData(typeof(CaseStatus))]
    [InlineData(typeof(CaseType))]
    [InlineData(typeof(CaseCategory))]
    [InlineData(typeof(CaseDecision))]
    [InlineData(typeof(AppealOutcome))]
    public void Every_value_of_the_case_enums_is_described(Type enumType)
    {
        var missing = Enum.GetNames(enumType).Where(n => !Doc.Contains($"`{n}`")).ToList();

        Assert.True(missing.Count == 0, $"{enumType.Name}: не описаны в docs/FRONTEND-CASES.md: " + string.Join(", ", missing));
    }
}
