using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Options;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Api.DevTools;

/// <summary>
/// Подготовка данных для ручной проверки видеокомнаты: <c>POST /dev/seed-lesson</c> создаёт двух подтверждённых пользователей с анкетами, обмен и подтверждённый урок,
/// который начинается через две минуты. Подключается ТОЛЬКО в среде Development (см. Program.cs): в остальных средах маршрута нет вообще (404).
/// Всё идёт через настоящие сервисы (заявка, принятие, предложение и подтверждение урока), поэтому ставятся и настоящие фоновые задачи урока.
/// Пароли пользователей возвращаются в ответе: это тестовые учётные записи на локальной базе.
/// Чтобы урок можно было назначить на «через две минуты», сроки урока сокращены в <c>appsettings.Development.json</c> (секция <c>Lessons</c>).
/// </summary>
public static class LessonSeedDevTools
{
    public const int StartsInMinutes = 2;

    public static IEndpointRouteBuilder MapLessonSeedDevTools(this IEndpointRouteBuilder app)
    {
        // Не часть договора с фронтендом: в OpenAPI/Scalar не показываем.
        app.MapPost("/dev/seed-lesson", SeedAsync).ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> SeedAsync(
        HttpContext http,
        AppDbContext db,
        UserManager<User> users,
        ExchangeRequestService requests,
        LessonSessionService lessons,
        TimeProvider clock,
        IOptions<LessonTimingOptions> timing,
        IOptions<FrontendOptions> frontend,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";   // в ответе пароли

        // Урок «через две минуты» возможен, только если слот можно назначить так близко: в ТЗ это не раньше чем за час.
        if (timing.Value.MinLeadMinutes >= StartsInMinutes)
        {
            return Problem(StatusCodes.Status409Conflict, "lesson_timing_not_shortened",
                $"Слот можно назначить не раньше чем за {timing.Value.MinLeadMinutes} мин., а урок начнётся через {StartsInMinutes}. " +
                "Сократите сроки в appsettings.Development.json (секция Lessons: MinLeadMinutes = 1) и перезапустите приложение.");
        }

        var skills = await db.Skills.AsNoTracking()
            .Where(s => s.IsActive && s.Category!.IsActive)
            .OrderBy(s => s.NameEn).Select(s => s.Id).Take(2).ToListAsync(ct);
        if (skills.Count < 2)
        {
            return Problem(StatusCodes.Status409Conflict, "skill_catalog_empty", "В каталоге меньше двух навыков: сначала нужен стартовый каталог.");
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var teacher = await CreateUserAsync(users, db, $"dev-teacher-{suffix}@example.com", "Преподаватель (dev)", skills[0], skills[1], ct);
        var student = await CreateUserAsync(users, db, $"dev-student-{suffix}@example.com", "Ученик (dev)", skills[1], skills[0], ct);

        // Заявка и её принятие — те же сервисы, что у API.
        var request = await requests.CreateAsync(teacher.User.Id, new CreateExchangeRequestRequest(student.User.Id, skills[1], skills[0], 30, null), ct);
        var accepted = await requests.AcceptAsync(student.User.Id, request.Id, null, ct);
        var exchangeId = accepted.ExchangeId ?? throw new InvalidOperationException("Принятая заявка не вернула обмен.");

        var start = clock.GetUtcNow().AddMinutes(StartsInMinutes);
        var session = await lessons.ProposeAsync(teacher.User.Id, exchangeId, new ProposeSessionRequest(start, teacher.User.Id, "Проверка видеокомнаты (dev)"), ct);
        session = await lessons.ConfirmAsync(student.User.Id, session.Id, ct);

        var baseUrl = frontend.Value.BaseUrl.TrimEnd('/');
        return Results.Ok(new
        {
            exchangeId,
            sessionId = session.Id,
            roomName = VideoRoomService.RoomName(session.Id),
            startAt = session.StartAt,
            endAt = session.EndAt,
            joinOpensAt = session.StartAt - LessonSession.JoinOpensBefore,
            firstCheckAt = session.StartAt + LessonSession.FirstCheckAfterStart,
            roomClosesAt = session.EndAt + LessonSession.JoinClosesAfterEnd,
            users = new[]
            {
                new { role = "teacher", userId = teacher.User.Id, email = teacher.User.Email, password = teacher.Password, name = teacher.User.Name },
                new { role = "student", userId = student.User.Id, email = student.User.Email, password = student.Password, name = student.User.Name },
            },
            exchangeUrl = $"{baseUrl}/exchanges/{exchangeId}",
            hint = "Войдите двумя пользователями в разных браузерах (или в обычном окне и окне инкогнито), откройте обмен и урок. Вход в комнату открывается за "
                + $"{LessonSession.JoinOpensBefore.TotalMinutes:0} мин. до начала.",
        });
    }

    internal sealed record Seeded(User User, string Password);

    internal static async Task<Seeded> CreateUserAsync(
        UserManager<User> users, AppDbContext db, string email, string name, Guid teachSkill, Guid learnSkill, CancellationToken ct)
    {
        var password = $"Dev-{Guid.NewGuid():N}"[..16] + "1a";
        var user = User.CreateRegular(email, name, new DateOnly(1995, 5, 5), "ru");
        user.UpdateProfile(name, null, "ru", "Asia/Dushanbe", true);
        user.EmailConfirmed = true;

        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Не удалось создать тестового пользователя: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        db.UserSkills.Add(UserSkill.Create(user.Id, teachSkill, SkillDirection.Teach, SkillLevel.Beginner, null));
        db.UserSkills.Add(UserSkill.Create(user.Id, learnSkill, SkillDirection.Learn, SkillLevel.Beginner, null));
        db.UserLanguages.Add(UserLanguage.Create(user.Id, "en"));
        await db.SaveChangesAsync(ct);
        return new Seeded(user, password);
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
