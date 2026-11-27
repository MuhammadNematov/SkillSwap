using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Files;
using SkillSwap.Application.Options;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Api.DevTools;

/// <summary>
/// Постоянные обычные пользователи для ручной проверки (только Development и только с этого компьютера, см. <see cref="DevToolsAccess"/>):
/// <list type="bullet">
/// <item><c>POST /dev/seed-users</c> создаёт или приводит в порядок <c>dev-user-1…3@example.com</c>: имя, аватар, о себе, языки (tg, ru, en), навыки «учу» и «хочу изучить»,
/// подобранные так, что каждый подходит каждому в «Поиске партнёров». Между user-1 и user-2 держит активный обмен с перепиской и подтверждённым уроком (как <c>/dev/seed-lesson</c>).
/// Повторный вызов ничего не дублирует: адреса, пароль и обмен те же, урок создаётся заново, только если открытого нет.</item>
/// <item><c>POST /dev/login-user</c> с <c>{ "email": "dev-user-1@example.com" }</c> — вход без пароля и капчи: настоящий сеанс (access-токен в ответе, refresh в cookie), как после <c>/auth/login</c>.</item>
/// <item><c>POST /dev/reset-lesson</c> отменяет открытые уроки user-1 и user-2 (без виновного, как при остановке обмена) и назначает новый, который начнётся примерно через минуту.</item>
/// </list>
/// Всё идёт через настоящие сервисы (заявка, принятие, сообщения, предложение и подтверждение урока), поэтому ставятся и настоящие уведомления и фоновые задачи.
/// </summary>
public static class DevUsersDevTools
{
    public const string Password = "Dev-user-pass-1";
    public const int LessonStartsInMinutes = 2;
    public const int ResetStartsInMinutes = 1;

    // Запас к «через минуту»: слот нельзя назначить ближе, чем Lessons:MinLeadMinutes, а пока запрос идёт, время уже немного ушло
    private static readonly TimeSpan StartMargin = TimeSpan.FromSeconds(10);

    internal sealed record DevUser(string Email, string Name, string Bio, string[] Teach, string[] Learn, uint ColorFrom, uint ColorTo);

    // Каждый учит одному и хочет изучить то, чему учат двое других: каждая пара подходит друг другу в обе стороны.
    // Если какого-то навыка нет в каталоге, берётся навык по порядку (см. ResolveSkillsAsync).
    internal static readonly DevUser[] Users =
    [
        new("dev-user-1@example.com", "Алишер (dev 1)", "Преподаю английский, хочу освоить Python и Figma. Тестовый пользователь разработки.", ["English"], ["Python", "Figma"], 0xFF6841E8, 0xFFB9A8F5),
        new("dev-user-2@example.com", "Мадина (dev 2)", "Пишу на Python, хочу подтянуть английский и дизайн. Тестовый пользователь разработки.", ["Python"], ["English", "Figma"], 0xFF12A150, 0xFF8BE0B0),
        new("dev-user-3@example.com", "Фаррух (dev 3)", "Дизайнер в Figma, учу английский и Python. Тестовый пользователь разработки.", ["Figma"], ["English", "Python"], 0xFFE8892A, 0xFFF7D08A),
    ];

    private static readonly string[] Languages = ["tg", "ru", "en"];

    private static readonly string[] Conversation =
    [
        "Привет! Готов начать обмен: английский от меня, Python от тебя.",
        "Привет! Отлично, давай. Первый урок проведёшь ты?",
        "Да, предложу время прямо здесь. Захвати вопросы по грамматике.",
    ];

    public static IEndpointRouteBuilder MapDevUsersDevTools(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/seed-users", SeedAsync).ExcludeFromDescription();
        app.MapPost("/dev/login-user", LoginAsync).ExcludeFromDescription();
        app.MapPost("/dev/reset-lesson", ResetLessonAsync).ExcludeFromDescription();
        return app;
    }

    public sealed record LoginUserRequest(string? Email);

    // ---------- пользователи ----------

    private static async Task<IResult> SeedAsync(HttpContext http, AppDbContext db, TimeProvider clock, IOptions<LessonTimingOptions> timing, IOptions<FrontendOptions> frontend, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";   // в ответе пароль
        var pair = await EnsureAsync(http, db, ct);
        if (pair.Problem is not null)
        {
            return pair.Problem;
        }

        // Урок, как у /dev/seed-lesson: только если открытого (подтверждённого и ещё не закрытого) нет и сроки урока сокращены
        LessonSessionDto? lesson = null;
        string? lessonNote = null;
        var now = clock.GetUtcNow().UtcDateTime;
        var open = await db.Sessions.AsNoTracking()
            .Where(s => s.ExchangeId == pair.ExchangeId && (s.Status == SessionStatus.Scheduled || s.Status == SessionStatus.InProgress))
            .OrderBy(s => s.StartAt).ToListAsync(ct);
        var current = open.FirstOrDefault(s => s.RoomClosesAt > now);
        if (current is null)
        {
            if (timing.Value.MinLeadMinutes < LessonStartsInMinutes)
            {
                lesson = await CreateLessonAsync(http, pair, clock.GetUtcNow().AddMinutes(LessonStartsInMinutes), ct);
            }
            else
            {
                lessonNote = TimingNote(timing.Value.MinLeadMinutes, LessonStartsInMinutes);
            }
        }

        var lessonView = lesson is not null
            ? Lesson(lesson.Id, lesson.StartAt, created: true)
            : current is not null ? Lesson(current.Id, current.StartAt, created: false) : null;
        return Results.Ok(Describe(pair, lessonView, lessonNote, frontend.Value));
    }

    private static async Task<IResult> LoginAsync(HttpContext http, LoginUserRequest? body, UserManager<User> users, AuthTokenService tokens, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var email = body?.Email?.Trim();
        // Только три постоянных пользователя: панель не должна открывать вход в любой аккаунт без пароля
        var known = Users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
        var user = known is null ? null : await users.FindByEmailAsync(known.Email);
        if (user is null || !user.IsRegular || user.Status == UserStatus.Deleted)
        {
            return Problem(StatusCodes.Status404NotFound, "dev_user_not_found", "Нет такого dev-пользователя: сначала «Получить пользователей» (POST /dev/seed-users).");
        }

        if (user.IsBlocked)
        {
            return Problem(StatusCodes.Status403Forbidden, "account_blocked", "Аккаунт заблокирован: снимите блокировку в админке или вызовите /dev/seed-users.");
        }

        var result = await tokens.IssueAsync(user, new ClientInfo(http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString()), ct);
        RefreshCookie.Set(http.Response, result.RefreshToken, result.RefreshTokenExpiresAt);
        return Results.Ok(result.Response);
    }

    private static async Task<IResult> ResetLessonAsync(HttpContext http, AppDbContext db, TimeProvider clock, IOptions<LessonTimingOptions> timing, IOptions<FrontendOptions> frontend, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (timing.Value.MinLeadMinutes > ResetStartsInMinutes)
        {
            return Problem(StatusCodes.Status409Conflict, "lesson_timing_not_shortened", TimingNote(timing.Value.MinLeadMinutes, ResetStartsInMinutes));
        }

        var pair = await EnsureAsync(http, db, ct);
        if (pair.Problem is not null)
        {
            return pair.Problem;
        }

        var start = clock.GetUtcNow() + TimeSpan.FromMinutes(Math.Max(ResetStartsInMinutes, timing.Value.MinLeadMinutes)) + StartMargin;
        var end = start.UtcDateTime + TimeSpan.FromMinutes(timing.Value.DurationMinutes);

        // Идущий урок отменить нельзя (так устроены статусы), а новый пересёкся бы с ним: просим подождать его конца
        var running = await db.Sessions.AsNoTracking()
            .Where(s => s.ExchangeId == pair.ExchangeId && s.Status == SessionStatus.InProgress && s.StartAt < end && s.EndAt > start.UtcDateTime)
            .OrderByDescending(s => s.EndAt).FirstOrDefaultAsync(ct);
        if (running is not null)
        {
            return Problem(StatusCodes.Status409Conflict, "dev_lesson_in_progress",
                $"Урок ещё идёт (до {running.EndAt:HH:mm} UTC): идущий урок отменить нельзя. Сбросьте после его окончания.", new() { ["endsAt"] = running.EndAt });
        }

        // Открытые уроки снимаются без виновного (как при остановке обмена): на надёжность пользователей это не влияет
        var cancelled = 0;
        foreach (var session in await db.Sessions.Where(s => s.ExchangeId == pair.ExchangeId && (s.Status == SessionStatus.Proposed || s.Status == SessionStatus.Scheduled)).ToListAsync(ct))
        {
            if (session.CancelByExchange())
            {
                cancelled++;
            }
        }

        await db.SaveChangesAsync(ct);

        var lesson = await CreateLessonAsync(http, pair, start, ct);
        return Results.Ok(new
        {
            exchangeId = pair.ExchangeId,
            cancelled,
            lesson = Lesson(lesson.Id, lesson.StartAt, created: true),
            exchangeUrl = frontend.Value.ExchangeUrl(pair.ExchangeId),
        });
    }

    // ---------- общее ----------

    internal sealed record Pair(IReadOnlyList<User> Users, Guid ExchangeId, Guid TeachSkillId, bool ExchangeCreated, IResult? Problem, bool AvatarsSkipped = false);

    /// <summary>Пользователи в нужном виде и активный обмен user-1 ↔ user-2 (с перепиской, если обмен создан сейчас).</summary>
    private static async Task<Pair> EnsureAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var skills = await ResolveSkillsAsync(db, ct);
        if (skills is null)
        {
            return new Pair([], Guid.Empty, Guid.Empty, false,
                Problem(StatusCodes.Status409Conflict, "skill_catalog_empty", "В каталоге меньше трёх навыков: сначала нужен стартовый каталог."));
        }

        var services = http.RequestServices;
        var accounts = new List<User>();
        var avatarsSkipped = false;
        foreach (var spec in Users)
        {
            var (account, avatarSkipped) = await EnsureUserAsync(services, db, spec, skills, ct);
            accounts.Add(account);
            avatarsSkipped |= avatarSkipped;
        }

        var (first, second) = (accounts[0], accounts[1]);
        var exchangeId = await db.Exchanges.AsNoTracking()
            .Where(x => x.Status == ExchangeStatus.Active && ((x.UserAId == first.Id && x.UserBId == second.Id) || (x.UserAId == second.Id && x.UserBId == first.Id)))
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        var created = false;
        if (exchangeId is null)
        {
            var requests = services.GetRequiredService<ExchangeRequestService>();
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
            // Ожидающая заявка между ними (например, отправленная вручную) принимается, иначе новая заявка user-1 → user-2
            var pending = await db.ExchangeRequests.AsNoTracking()
                .Where(r => r.Status == ExchangeRequestStatus.Pending && r.ExpiresAt > now
                    && ((r.FromUserId == first.Id && r.ToUserId == second.Id) || (r.FromUserId == second.Id && r.ToUserId == first.Id)))
                .Select(r => new { r.Id, r.ToUserId }).FirstOrDefaultAsync(ct);
            var request = pending is not null
                ? (pending.Id, pending.ToUserId)
                : ((await requests.CreateAsync(first.Id, new CreateExchangeRequestRequest(second.Id, skills[Users[1].Teach[0]], skills[Users[0].Teach[0]], 30, "Давай меняться (dev)."), ct)).Id, second.Id);
            var accepted = await requests.AcceptAsync(request.Item2, request.Item1, new AcceptExchangeRequestRequest(skills[Users[0].Teach[0]]), ct);
            exchangeId = accepted.ExchangeId ?? throw new InvalidOperationException("Принятая заявка не вернула обмен.");
            created = true;

            var chat = services.GetRequiredService<ChatService>();
            for (var i = 0; i < Conversation.Length; i++)
            {
                await chat.SendAsync(i % 2 == 0 ? first.Id : second.Id, exchangeId.Value, Conversation[i], ct);
            }
        }

        return new Pair(accounts, exchangeId.Value, skills[Users[0].Teach[0]], created, null, avatarsSkipped);
    }

    /// <summary>Урок, который ведёт user-1: предлагает он, подтверждает user-2 (настоящие сервисы: карточка в чате, задачи урока, напоминания).</summary>
    private static async Task<LessonSessionDto> CreateLessonAsync(HttpContext http, Pair pair, DateTimeOffset start, CancellationToken ct)
    {
        var lessons = http.RequestServices.GetRequiredService<LessonSessionService>();
        var (teacher, student) = (pair.Users[0], pair.Users[1]);
        var proposed = await lessons.ProposeAsync(teacher.Id, pair.ExchangeId, new ProposeSessionRequest(start, teacher.Id, "Урок английского (dev)"), ct);
        return await lessons.ConfirmAsync(student.Id, proposed.Id, ct);
    }

    /// <returns>Пользователь и признак «аватар не загружен» (хранилище недоступно: например, не запущен Docker); без аватара всё остальное работает.</returns>
    private static async Task<(User User, bool AvatarSkipped)> EnsureUserAsync(IServiceProvider services, AppDbContext db, DevUser spec, IReadOnlyDictionary<string, Guid> skills, CancellationToken ct)
    {
        var users = services.GetRequiredService<UserManager<User>>();
        var user = await users.FindByEmailAsync(spec.Email);
        if (user is null)
        {
            user = User.CreateRegular(spec.Email, spec.Name, new DateOnly(1996, 4, 12), "ru");
            user.EmailConfirmed = true;
            var result = await users.CreateAsync(user, Password);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException("Не удалось создать dev-пользователя: " + string.Join(", ", result.Errors.Select(e => e.Code)));
            }
        }
        else
        {
            if (!user.EmailConfirmed)
            {
                user.EmailConfirmed = true;
                await users.UpdateAsync(user);
            }

            if (!await users.CheckPasswordAsync(user, Password))
            {
                // Пароль сменили на странице настроек: возвращаем известный, чтобы можно было войти и обычной формой
                if (await users.HasPasswordAsync(user))
                {
                    await users.RemovePasswordAsync(user);
                }

                await users.AddPasswordAsync(user, Password);
            }
        }

        var tracked = await db.Users.SingleAsync(u => u.Id == user.Id, ct);
        tracked.UpdateProfile(spec.Name, spec.Bio, tracked.UiLanguage, "Asia/Dushanbe", true);

        // Языки и навыки приводятся ровно к описанным: то, что добавили руками во время проверки, убирается
        db.UserLanguages.RemoveRange(await db.UserLanguages.Where(l => l.UserId == user.Id && !Languages.Contains(l.LanguageCode)).ToListAsync(ct));
        var haveLanguages = await db.UserLanguages.Where(l => l.UserId == user.Id).Select(l => l.LanguageCode).ToListAsync(ct);
        foreach (var code in Languages.Except(haveLanguages))
        {
            db.UserLanguages.Add(UserLanguage.Create(user.Id, code));
        }

        var wanted = spec.Teach.Select(n => (Skill: skills[n], Direction: SkillDirection.Teach, Level: SkillLevel.Advanced))
            .Concat(spec.Learn.Select(n => (Skill: skills[n], Direction: SkillDirection.Learn, Level: SkillLevel.Beginner)))
            .ToList();
        var current = await db.UserSkills.Where(s => s.UserId == user.Id).ToListAsync(ct);
        foreach (var skill in current)
        {
            var match = wanted.FirstOrDefault(w => w.Skill == skill.SkillId && w.Direction == skill.Direction);
            if (match == default)
            {
                db.UserSkills.Remove(skill);
            }
            else if (skill.Level != match.Level)
            {
                skill.Update(match.Level, skill.Description);
            }
        }

        foreach (var w in wanted.Where(w => !current.Any(c => c.SkillId == w.Skill && c.Direction == w.Direction)))
        {
            db.UserSkills.Add(UserSkill.Create(user.Id, w.Skill, w.Direction, w.Level, null));
        }

        await db.SaveChangesAsync(ct);

        // Аватар загружается тем же сервисом, что и у пользователя (раскодирование, квадрат 512, WebP); только если его нет: каждый вызов не плодит файлы
        if (tracked.AvatarKey is null)
        {
            var png = AvatarPng(spec.ColorFrom, spec.ColorTo);
            await using var stream = new MemoryStream(png, writable: false);
            try
            {
                await services.GetRequiredService<AvatarService>().SetAsync(user.Id, new UploadedFile("avatar.png", png.Length, stream), ct);
            }
            catch (ServiceUnavailableException)
            {
                return (tracked, true);   // загрузится при следующем вызове, когда хранилище поднимется
            }
        }

        return (tracked, false);
    }

    /// <summary>Навыки по английскому названию; если какого-то нет (другой каталог), подставляются первые по алфавиту, чтобы связки «учу ↔ хочу» сохранились.</summary>
    private static async Task<IReadOnlyDictionary<string, Guid>?> ResolveSkillsAsync(AppDbContext db, CancellationToken ct)
    {
        var names = Users.SelectMany(u => u.Teach.Concat(u.Learn)).Distinct().ToList();
        var catalog = await db.Skills.AsNoTracking().Where(s => s.IsActive && s.Category!.IsActive).OrderBy(s => s.NameEn).Select(s => new { s.Id, s.NameEn }).ToListAsync(ct);
        if (catalog.Count < names.Count)
        {
            return null;
        }

        var result = new Dictionary<string, Guid>();
        var spare = catalog.Where(c => !names.Contains(c.NameEn)).Select(c => c.Id).GetEnumerator();
        foreach (var name in names)
        {
            var found = catalog.FirstOrDefault(c => c.NameEn == name);
            result[name] = found?.Id ?? (spare.MoveNext() ? spare.Current : throw new InvalidOperationException("Не хватило навыков в каталоге."));
        }

        return result;
    }

    /// <summary>Аватар без шрифтов (на сервере их может не быть): градиент и силуэт, у каждого пользователя свои цвета.</summary>
    private static byte[] AvatarPng(uint from, uint to)
    {
        const int size = 512;
        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        var canvas = surface.Canvas;
        using (var background = new SKPaint { IsAntialias = true })
        {
            background.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(size, size), [new SKColor(from), new SKColor(to)], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, size, size, background);
        }

        using var figure = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 225) };
        canvas.DrawCircle(size / 2f, 205, 92, figure);
        canvas.DrawOval(new SKRect(96, 320, size - 96, 600), figure);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static object Lesson(Guid id, DateTime startAt, bool created) => new { sessionId = id, startAt = DateTime.SpecifyKind(startAt, DateTimeKind.Utc), created };

    private static object Describe(Pair pair, object? lesson, string? lessonNote, FrontendOptions frontend) => new
    {
        users = pair.Users.Select((u, i) => new { key = $"user{i + 1}", userId = u.Id, email = u.Email, name = u.Name, password = Password }).ToArray(),
        exchange = new { id = pair.ExchangeId, created = pair.ExchangeCreated, url = frontend.ExchangeUrl(pair.ExchangeId) },
        lesson,
        lessonNote,
        avatarNote = pair.AvatarsSkipped ? "Аватары не загружены: хранилище файлов недоступно (docker compose up -d). Вызовите /dev/seed-users ещё раз, когда оно поднимется." : null,
        hint = "Вход без пароля: POST /dev/login-user { email }. Пароль у всех один (поле password): можно войти и обычной формой, например с телефона. "
            + "Урок user-1 и user-2 заново: POST /dev/reset-lesson (начнётся примерно через минуту).",
    };

    private static string TimingNote(int minLead, int startsIn) =>
        $"Слот можно назначить не раньше чем за {minLead} мин., а урок должен начаться через {startsIn}. Сократите сроки в appsettings.Development.json (секция Lessons: MinLeadMinutes = 1) и перезапустите приложение.";

    private static IResult Problem(int status, string code, string detail, Dictionary<string, object?>? extra = null)
    {
        var extensions = extra ?? [];
        extensions["code"] = code;
        return Results.Problem(statusCode: status, title: detail, extensions: extensions);
    }
}
