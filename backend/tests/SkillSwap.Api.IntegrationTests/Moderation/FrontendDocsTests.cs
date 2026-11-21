using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>
/// Сводка для фронтенда (<c>docs/FRONTEND-CASES.md</c>) не должна отставать от кода: каждый тип уведомления этапа 6, каждый статус, решение, тип и категория обращения
/// в ней упомянуты. Добавили значение и не описали его, тест падает.
/// </summary>
public class FrontendDocsTests
{
    /// <summary>Обе сводки вместе: уведомления этапа 7 описаны в docs/FRONTEND-ADMIN.md, этапа 6 в docs/FRONTEND-CASES.md.</summary>
    private static readonly string Doc = File.ReadAllText(DocPath("FRONTEND-CASES.md")) + Environment.NewLine + File.ReadAllText(DocPath("FRONTEND-ADMIN.md"));

    private static string DocPath(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs")) && Directory.Exists(Path.Combine(dir.FullName, "backend")))
            {
                return Path.Combine(dir.FullName, "docs", name);
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория (каталоги docs и backend).");
    }

    [Fact]
    public void Every_notification_type_from_stage_six_on_is_described()
    {
        var all = Enum.GetValues<NotificationType>().ToList();
        var stageSix = all.Skip(all.IndexOf(NotificationType.ReviewAvailable)).ToList();

        var missing = stageSix.Where(t => !Doc.Contains($"`{t}`")).ToList();

        Assert.True(missing.Count == 0, "Не описаны в docs/FRONTEND-CASES.md или docs/FRONTEND-ADMIN.md: " + string.Join(", ", missing));
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
