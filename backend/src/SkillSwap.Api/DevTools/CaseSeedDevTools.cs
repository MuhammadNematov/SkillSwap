using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Options;
using SkillSwap.Application.Reviews;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace SkillSwap.Api.DevTools;

/// <summary>На какой стадии оставить обращение при <c>POST /dev/seed-case</c>.</summary>
public enum SeedCaseStage
{
    /// <summary>Подано, ждёт модератора (очередь).</summary>
    Open,

    /// <summary>Взято модератором, доступ к чату выдан.</summary>
    Taken,

    /// <summary>Модератор запросил пояснения у обеих сторон (ждём ответов).</summary>
    Waiting,

    /// <summary>Решено (идёт срок апелляции).</summary>
    Resolved,

    /// <summary>Подана апелляция, ждёт админа.</summary>
    Appealed,

    /// <summary>Админ назначил проверяющего, идёт повторная проверка.</summary>
    AppealReview,
}

/// <param name="Type">Тип обращения: <c>Conduct</c> (по умолчанию), <c>NoShowAbuse</c>, <c>ReviewDispute</c>.</param>
/// <param name="Stage">Стадия (<see cref="SeedCaseStage"/>), по умолчанию <c>Open</c>.</param>
/// <param name="Decision">Решение для стадий «решено» и дальше; по умолчанию <c>Warning</c> (для спора <c>ReviewHidden</c>).</param>
public sealed record SeedCaseRequest(string? Type, string? Stage, string? Decision);

/// <summary>
/// Данные для ручной проверки обращений и модерации (только Development, как и <c>/dev/seed-lesson</c>): <c>POST /dev/seed-staff</c> создаёт (при первом вызове) модераторов и админа
/// и выдаёт на них токены; <c>POST /dev/seed-case</c> создаёт двух пользователей, обмен с перепиской и обращение, доведённое до нужной стадии <b>через настоящие сервисы</b>
/// (подача, взятие, вопросы, решение, апелляция, назначение проверяющего), поэтому ставятся и настоящие уведомления, письма и фоновые задачи.
/// Служебные аккаунты входят через 2FA, а в разработке настраивать её каждый раз неудобно, поэтому вместо входа выдаются access-токены (живут недолго: вызовите маршрут снова).
/// Сроки (ответ на вопрос, апелляция, срок реакции) сокращаются в <c>appsettings.Development.json</c> (секция <c>Cases</c>).
/// </summary>
public static class CaseSeedDevTools
{
    public const string ModeratorEmail = "dev-moderator@example.com";
    public const string SecondModeratorEmail = "dev-moderator-2@example.com";
    public const string AdminEmail = "dev-admin@example.com";

    private static readonly string[] ConductMessages =
    [
        "Привет! Когда продолжим урок?",
        "Ты вообще ничего не понимаешь в этом, сначала научись сам.",
        "Ещё раз пропустишь урок, пожалеешь. Не пиши мне больше.",
    ];

    public static IEndpointRouteBuilder MapCaseSeedDevTools(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/seed-staff", SeedStaffAsync).ExcludeFromDescription();
        app.MapPost("/dev/seed-case", SeedCaseAsync).ExcludeFromDescription();
        return app;
    }

    // ---------- служебные аккаунты ----------

    private static async Task<IResult> SeedStaffAsync(HttpContext http, UserManager<User> users, IAccessTokenGenerator tokens, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var moderator = await EnsureStaffAsync(users, ModeratorEmail, "Модератор (dev)", UserRole.Moderator);
        var second = await EnsureStaffAsync(users, SecondModeratorEmail, "Модератор 2 (dev)", UserRole.Moderator);
        var admin = await EnsureStaffAsync(users, AdminEmail, "Админ (dev)", UserRole.Admin);
        return Results.Ok(new { moderator = Describe(moderator, tokens), secondModerator = Describe(second, tokens), admin = Describe(admin, tokens), hint = StaffHint });
    }

    private const string StaffHint =
        "Служебные аккаунты входят через 2FA, поэтому для разработки выданы access-токены (Authorization: Bearer ...). Пароль виден только при создании аккаунта; токены живут недолго: вызовите маршрут снова.";

    private sealed record Staff(User User, string? Password);

    private static async Task<Staff> EnsureStaffAsync(UserManager<User> users, string email, string name, UserRole role)
    {
        if (await users.FindByEmailAsync(email) is { } existing)
        {
            return new Staff(existing, null);
        }

        var password = $"Dev-{Guid.NewGuid():N}"[..16] + "1a";
        var user = User.CreateStaff(email, name, role);
        user.EmailConfirmed = true;
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Не удалось создать служебного пользователя: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        return new Staff(user, password);
    }

    private static object Describe(Staff staff, IAccessTokenGenerator tokens) => new
    {
        userId = staff.User.Id,
        email = staff.User.Email,
        name = staff.User.Name,
        role = staff.User.Role.ToString(),
        password = staff.Password,
        accessToken = tokens.Create(staff.User, Guid.NewGuid()).Value,
    };

    // ---------- обращение ----------

    private static async Task<IResult> SeedCaseAsync(
        HttpContext http,
        SeedCaseRequest? request,
        AppDbContext db,
        UserManager<User> users,
        IAccessTokenGenerator tokens,
        ExchangeRequestService requests,
        ChatService chat,
        CaseService cases,
        ModerationService moderation,
        ExplanationService explanations,
        ResolutionService resolution,
        AppealService appeals,
        AdminCaseService admins,
        ReviewService reviews,
        TimeProvider clock,
        IOptions<CaseTimingOptions> timing,
        IOptions<FrontendOptions> frontend,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!Enum.TryParse<CaseType>(request?.Type ?? nameof(CaseType.Conduct), ignoreCase: true, out var type) || !Enum.IsDefined(type))
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_seed_type", "type: Conduct, NoShowAbuse или ReviewDispute.");
        }

        if (!Enum.TryParse<SeedCaseStage>(request?.Stage ?? nameof(SeedCaseStage.Open), ignoreCase: true, out var stage) || !Enum.IsDefined(stage))
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_seed_stage", "stage: Open, Taken, Waiting, Resolved, Appealed или AppealReview.");
        }

        var defaultDecision = type == CaseType.ReviewDispute ? CaseDecision.ReviewHidden : CaseDecision.Warning;
        var decision = defaultDecision;
        if (request?.Decision is { } text && (!Enum.TryParse<CaseDecision>(text, ignoreCase: true, out decision) || !CaseRules.DecisionsFor(type).Contains(decision)))
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_seed_decision", $"decision для {type}: {string.Join(", ", CaseRules.DecisionsFor(type))}.");
        }

        var skills = await db.Skills.AsNoTracking().Where(s => s.IsActive && s.Category!.IsActive).OrderBy(s => s.NameEn).Select(s => s.Id).Take(2).ToListAsync(ct);
        if (skills.Count < 2)
        {
            return Problem(StatusCodes.Status409Conflict, "skill_catalog_empty", "В каталоге меньше двух навыков: сначала нужен стартовый каталог.");
        }

        var moderator = await EnsureStaffAsync(users, ModeratorEmail, "Модератор (dev)", UserRole.Moderator);
        var admin = await EnsureStaffAsync(users, AdminEmail, "Админ (dev)", UserRole.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var opener = await LessonSeedDevTools.CreateUserAsync(users, db, $"dev-opener-{suffix}@example.com", "Заявитель (dev)", skills[0], skills[1], ct);
        var accused = await LessonSeedDevTools.CreateUserAsync(users, db, $"dev-accused-{suffix}@example.com", "Ответчик (dev)", skills[1], skills[0], ct);

        var request0 = await requests.CreateAsync(opener.User.Id, new CreateExchangeRequestRequest(accused.User.Id, skills[1], skills[0], 30, null), ct);
        var exchangeId = (await requests.AcceptAsync(accused.User.Id, request0.Id, null, ct)).ExchangeId ?? throw new InvalidOperationException("Принятая заявка не вернула обмен.");

        // Переписка: сообщения ответчика (последние два становятся доказательствами); спору по отзыву переписка не нужна, но чат не пустой.
        var sent = new List<Guid>();
        foreach (var line in ConductMessages)
        {
            sent.Add((await chat.SendAsync(accused.User.Id, exchangeId, line, ct)).Id);
        }

        Guid? reviewId = null;
        if (type == CaseType.ReviewDispute)
        {
            reviewId = await PublishReviewAboutOpenerAsync(db, reviews, opener.User.Id, accused.User.Id, exchangeId, clock, ct);
        }

        var created = await cases.OpenAsync(opener.User.Id, new OpenCaseRequest(
            type, FirstCategory(type), exchangeId, reviewId, "Собеседник грубит в чате и срывает договорённости (данные для проверки, dev).",
            type == CaseType.ReviewDispute ? null : sent.TakeLast(2).ToList()), ct);
        var caseId = created.Id;

        Guid? secondModeratorId = null;
        if (stage >= SeedCaseStage.Taken)
        {
            await moderation.TakeAsync(moderator.User.Id, caseId, ct);
        }

        if (stage == SeedCaseStage.Waiting)
        {
            await explanations.AskAsync(moderator.User.Id, caseId, new AskQuestionRequest(ExplanationRecipient.Both, "Опишите, пожалуйста, что произошло на уроке (dev)."), ct);
        }

        if (stage >= SeedCaseStage.Resolved)
        {
            await resolution.ResolveAsync(moderator.User.Id, caseId, new ResolveCaseRequest(decision, false, "Решение для проверки интерфейса: обоснование из сидера (dev)."), ct);
        }

        if (stage >= SeedCaseStage.Appealed)
        {
            await appeals.FileAsync(accused.User.Id, caseId, new FileAppealRequest("Не согласен с решением, прошу перепроверить (dev)."), ct);
        }

        if (stage >= SeedCaseStage.AppealReview)
        {
            var second = await EnsureStaffAsync(users, SecondModeratorEmail, "Модератор 2 (dev)", UserRole.Moderator);
            secondModeratorId = second.User.Id;
            await admins.AssignReviewerAsync(admin.User.Id, caseId, new AssignReviewerRequest(second.User.Id), ct);
        }

        var secondStaff = secondModeratorId is { } id ? await users.FindByIdAsync(id.ToString()) : null;
        var settings = CaseTiming.Current;
        var baseUrl = frontend.Value.BaseUrl.TrimEnd('/');
        return Results.Ok(new
        {
            caseId,
            type = type.ToString(),
            stage = stage.ToString(),
            exchangeId,
            reviewId,
            evidenceMessageIds = type == CaseType.ReviewDispute ? Array.Empty<Guid>() : sent.TakeLast(2).ToArray(),
            caseUrl = $"{baseUrl}/cases/{caseId}",
            opener = Regular("opener", opener, tokens),
            accused = Regular("accused", accused, tokens),
            moderator = Describe(moderator, tokens),
            admin = Describe(admin, tokens),
            secondModerator = secondStaff is null ? null : Describe(new Staff(secondStaff, null), tokens),
            timers = new
            {
                highSlaMinutes = settings.HighPrioritySla.TotalMinutes,
                normalSlaMinutes = settings.NormalPrioritySla.TotalMinutes,
                explanationWindowMinutes = settings.ExplanationWindow.TotalMinutes,
                explanationReminderBeforeMinutes = settings.ExplanationReminderBefore.TotalMinutes,
                appealWindowMinutes = settings.AppealWindow.TotalMinutes,
                shortened = !timing.Value.IsDefault,
            },
            hint = "Заявитель и ответчик входят по почте и паролю; модератор и админ по выданным токенам (маршрут повторяется, аккаунты создаются один раз). Сроки сокращены в appsettings.Development.json (секция Cases).",
        });
    }

    private static CaseCategory FirstCategory(CaseType type) => CaseRules.CategoriesFor(type)[0];

    private static object Regular(string role, LessonSeedDevTools.Seeded seeded, IAccessTokenGenerator tokens) => new
    {
        role,
        userId = seeded.User.Id,
        email = seeded.User.Email,
        password = seeded.Password,
        name = seeded.User.Name,
        accessToken = tokens.Create(seeded.User, Guid.NewGuid()).Value,
    };

    /// <summary>
    /// Для спора по отзыву: обмен закрывается с одним засчитанным уроком, оба пишут отзыв (публикуются сразу), оспаривается отзыв ответчика о заявителе.
    /// Урок и закрытие обмена записываются напрямую (иначе пришлось бы ждать настоящий урок), отзывы идут через настоящий сервис.
    /// </summary>
    private static async Task<Guid> PublishReviewAboutOpenerAsync(
        AppDbContext db, ReviewService reviews, Guid openerId, Guid accusedId, Guid exchangeId, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lesson = LessonSession.Propose(exchangeId, openerId, openerId, now.AddHours(5), null, now, now.AddDays(30));
        db.Sessions.Add(lesson);
        await db.SaveChangesAsync(ct);
        var start = now.AddDays(-5);
        await db.Sessions.Where(s => s.Id == lesson.Id).ExecuteUpdateAsync(s => s
            .SetProperty(v => v.StartAt, start).SetProperty(v => v.EndAt, start.AddMinutes(45)).SetProperty(v => v.Status, SessionStatus.Completed), ct);
        await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, now.AddDays(-1)), ct);

        await reviews.CreateAsync(openerId, exchangeId, new ReviewRequest(5, 5, 5, 5, "Хороший обмен, всё понятно объяснил (dev)."), ct);
        await reviews.CreateAsync(accusedId, exchangeId, new ReviewRequest(1, 2, 1, 1, "Слабый урок, ничего не объяснил и опоздал (dev)."), ct);
        return await db.Reviews.AsNoTracking().Where(r => r.ExchangeId == exchangeId && r.AuthorId == accusedId).Select(r => r.Id).SingleAsync(ct);
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
