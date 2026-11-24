using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Options;
using SkillSwap.Application.Tests.Auth;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Tests.Contracts;

/// <summary>
/// Договор бэкенда и фронтенда: каждая ссылка из письма (настройки <c>Frontend:*Path</c> и ссылка сброса пароля) должна вести
/// на существующий маршрут фронтенда. Маршруты читаются из <c>frontend/src/App.tsx</c>, а пути ссылок берутся так же, как их строит сервер:
/// значения по умолчанию из <see cref="FrontendOptions"/> и переопределения из <c>appsettings.json</c>.
/// Если ссылка ведёт на несуществующую страницу, человек из письма попадёт на «Страница не найдена».
/// </summary>
public class FrontendLinkRoutesTests
{
    private static readonly Guid SampleId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // ---------- маршруты фронтенда ----------

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "frontend", "src", "App.tsx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не нашёл frontend/src/App.tsx выше папки тестов: тест соответствия ссылок запускается из репозитория целиком.");
    }

    /// <summary>
    /// Шаблоны маршрутов из JSX: строка за строкой, вложенность по открывающим и закрывающим &lt;Route&gt;.
    /// Рассчитано на то, что каждый &lt;Route&gt; стоит на своей строке (так оформлен App.tsx); тест ниже проверяет, что разбор что-то нашёл.
    /// </summary>
    internal static IReadOnlyList<string> FrontendRoutes()
    {
        var routes = new List<string>();
        var prefixes = new Stack<string>();
        prefixes.Push("");

        foreach (var raw in File.ReadAllLines(Path.Combine(RepoRoot(), "frontend", "src", "App.tsx")))
        {
            var line = raw.Trim();
            if (line.StartsWith("</Route>", StringComparison.Ordinal))
            {
                prefixes.Pop();
                continue;
            }

            if (!line.StartsWith("<Route", StringComparison.Ordinal))
            {
                continue;
            }

            var path = Regex.Match(line, "\\spath=\"([^\"]*)\"");
            var full = path.Success ? Join(prefixes.Peek(), path.Groups[1].Value) : prefixes.Peek();
            var opens = !line.EndsWith("/>", StringComparison.Ordinal);   // открывающий тег с вложенными маршрутами
            // Универсальный маршрут path="*" (страница «не найдена») подходит любой ссылке, поэтому настоящим маршрутом не считается.
            if (path.Success && path.Groups[1].Value != "*")
            {
                routes.Add(full);
            }

            if (opens)
            {
                prefixes.Push(full);
            }
        }

        return routes;
    }

    private static string Join(string prefix, string path) =>
        path.StartsWith('/') ? path : (prefix.TrimEnd('/') + "/" + path).Replace("//", "/", StringComparison.Ordinal);

    /// <summary>Маршрут React Router подходит ссылке: <c>:name</c> это один сегмент, <c>*</c> всё остальное.</summary>
    internal static bool Matches(string route, string linkPath)
    {
        var pattern = "^" + Regex.Escape(route.TrimEnd('/')).Replace("/\\*", "(/.*)?", StringComparison.Ordinal)
            .Replace("\\*", ".*", StringComparison.Ordinal) + "/?$";
        pattern = Regex.Replace(pattern, ":\\w+", "[^/]+");
        return Regex.IsMatch(linkPath.Length == 0 ? "/" : linkPath, pattern);
    }

    // ---------- ссылки из писем ----------

    /// <summary>Настройки как у сервера: значения по умолчанию плюс переопределения из appsettings.json (секция Frontend).</summary>
    private static FrontendOptions ServerFrontendOptions()
    {
        var options = new FrontendOptions { BaseUrl = "https://app.test" };
        var file = Path.Combine(RepoRoot(), "backend", "src", "SkillSwap.Api", "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        if (document.RootElement.TryGetProperty("Frontend", out var section))
        {
            foreach (var property in section.EnumerateObject())
            {
                var target = typeof(FrontendOptions).GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance);
                if (target is { CanWrite: true } && target.PropertyType == typeof(string) && property.Name != nameof(FrontendOptions.BaseUrl))
                {
                    target.SetValue(options, property.Value.GetString());
                }
            }
        }

        return options;
    }

    private static string PathOf(string url) => new Uri(url).AbsolutePath;

    public static TheoryData<string, string> EmailLinks()
    {
        var o = ServerFrontendOptions();
        return new TheoryData<string, string>
        {
            { "заявки (письмо «новая заявка»)", PathOf(o.RequestsUrl()) },
            { "обмен (письма о заявке, сообщениях, уроках)", PathOf(o.ExchangeUrl(SampleId)) },
            { "обращение (письма о решении и апелляции)", PathOf(o.CaseUrl(SampleId)) },
            { "приглашение сотрудника", PathOf(o.StaffInvitationUrl("sample-token")) },
            { "привязка личного аккаунта", PathOf(o.PersonalLinkUrl("sample-token")) },
            { "корень сайта (запасная ссылка, когда в уведомлении нет id)", PathOf(o.BaseUrl) },
        };
    }

    [Theory]
    [MemberData(nameof(EmailLinks))]
    public void Every_link_from_an_email_leads_to_an_existing_frontend_route(string what, string linkPath)
    {
        var routes = FrontendRoutes();

        Assert.True(routes.Any(r => Matches(r, linkPath)),
            $"Ссылка «{what}» ведёт на {linkPath}, а такого маршрута во frontend/src/App.tsx нет. " +
            $"Поменяйте маршрут фронтенда или настройку Frontend:*Path. Маршруты: {string.Join(", ", routes)}");
    }

    [Fact]
    public async Task The_password_reset_link_from_the_real_email_leads_to_an_existing_route()
    {
        var fx = new AuthFixture();
        var user = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "ru");
        user.EmailConfirmed = true;
        fx.Users.Existing = user;

        await fx.PasswordReset.ForgotPasswordAsync(new ForgotPasswordRequest("ali@example.com"));

        var body = Assert.Single(fx.Mail.Sent).Body;
        var link = Regex.Match(body, @"https?://\S+").Value;
        Assert.NotEmpty(link);
        Assert.Contains(FrontendRoutes(), r => Matches(r, PathOf(link)));
    }

    // ---------- сам разбор не пустой и не «пропускает всё» ----------

    [Fact]
    public void The_route_parser_finds_the_known_routes_including_nested_ones()
    {
        var routes = FrontendRoutes();

        Assert.True(routes.Count >= 15, $"Разобрано маршрутов: {routes.Count}. Формат App.tsx изменился? Нужно поправить разбор.");
        Assert.Contains("/login", routes);
        Assert.Contains("/app/exchanges/:id", routes);   // вложенный маршрут: /app + exchanges/:id
        Assert.Contains("/admin/*", routes);
    }

    [Theory]
    [InlineData("/staff-invitation/sample-token")]   // прежний неверный путь приглашения
    [InlineData("/no-such-page")]
    [InlineData("/app")]                              // этот маршрут есть: проверка в обе стороны
    public void The_matcher_is_not_vacuous(string linkPath)
    {
        var routes = FrontendRoutes();

        var matches = routes.Any(r => Matches(r, linkPath));

        Assert.Equal(linkPath == "/app", matches);
    }

    [Theory]
    [InlineData("/app/exchanges/abc", "/app/exchanges/:id", true)]
    [InlineData("/app/exchanges", "/app/exchanges/:id", false)]
    [InlineData("/app/exchanges/abc/extra", "/app/exchanges/:id", false)]
    [InlineData("/admin/cases/abc", "/admin/*", true)]
    [InlineData("/admin", "/admin/*", true)]
    [InlineData("/administrator", "/admin/*", false)]
    public void Route_patterns_follow_react_router_rules(string link, string route, bool expected) =>
        Assert.Equal(expected, Matches(route, link));
}
