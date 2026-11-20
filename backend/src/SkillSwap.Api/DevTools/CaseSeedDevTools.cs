using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Auth;
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

/// <param name="Email">Адрес служебного аккаунта разработки (например, dev-admin@example.com).</param>
public sealed record StaffCodeRequest(string? Email);

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
        app.MapPost("/dev/staff-code", StaffCodeAsync).ExcludeFromDescription();
        app.MapPost("/dev/seed-case", SeedCaseAsync).ExcludeFromDescription();
        return app;
    }

    // ---------- служебные аккаунты ----------

    /// <summary>Пароль служебных аккаунтов разработки: один для всех, чтобы экраны подтверждения действий можно было проверить в любой момент (маршрут существует только в Development).</summary>
    public const string StaffPassword = "Dev-staff-pass-1";

    private static async Task<IResult> SeedStaffAsync(HttpContext http, IAccessTokenGenerator tokens, TimeProvider clock, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var moderator = await EnsureStaffAsync(http, ModeratorEmail, "Модератор (dev)", UserRole.Moderator);
        var second = await EnsureStaffAsync(http, SecondModeratorEmail, "Модератор 2 (dev)", UserRole.Moderator);
        var admin = await EnsureStaffAsync(http, AdminEmail, "Админ (dev)", UserRole.Admin);
        return Results.Ok(new
        {
            moderator = Describe(moderator, tokens, clock),
            secondModerator = Describe(second, tokens, clock),
            admin = Describe(admin, tokens, clock),
            hint = StaffHint,
        });
    }

    private const string StaffHint =
        "Служебные аккаунты входят через 2FA, поэтому для разработки выданы access-токены (Authorization: Bearer ...). У всех один пароль (поле password), 2FA включена с известным ключом "
        + "(twoFactor.sharedKey): положите его в любое приложение-аутентификатор или берите код здесь. Подтверждение действия (confirmation: { password, code }) принимает код один раз, "
        + "поэтому в ответе два кода: текущий и следующий (codes), для третьего подождите до 30 секунд или запросите заново: curl -X POST http://localhost:5023/dev/staff-code "
        + "-H \"Content-Type: application/json\" -d '{\"email\":\"dev-admin@example.com\"}'. Резервные коды (recoveryCodes) показываются один раз, при включении 2FA. Токены живут недолго: вызовите маршрут снова.";

    private sealed record Staff(User User, string Key, IReadOnlyList<string>? RecoveryCodes);

    /// <summary>Создаёт (или приводит в порядок) служебный аккаунт разработки: известный пароль, включённая 2FA с ключом, который можно прочитать.</summary>
    private static async Task<Staff> EnsureStaffAsync(HttpContext http, string email, string name, UserRole role)
    {
        var users = http.RequestServices.GetRequiredService<UserManager<User>>();
        var recovery = http.RequestServices.GetRequiredService<IRecoveryCodeStore>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = User.CreateStaff(email, name, role);
            user.EmailConfirmed = true;
            var created = await users.CreateAsync(user, StaffPassword);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException("Не удалось создать служебного пользователя: " + string.Join(", ", created.Errors.Select(e => e.Code)));
            }
        }
        else if (!await users.CheckPasswordAsync(user, StaffPassword))
        {
            // Аккаунт создан прежней версией сидера со случайным паролем, которого никто не знает: приводим к известному.
            if (await users.HasPasswordAsync(user))
            {
                await users.RemovePasswordAsync(user);
            }

            await users.AddPasswordAsync(user, StaffPassword);
        }

        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }

        IReadOnlyList<string>? codes = null;
        if (!user.TwoFactorEnabled)
        {
            await users.SetTwoFactorEnabledAsync(user, true);
            codes = RecoveryCodes.Generate(8);
            await recovery.ReplaceAsync(user.Id, codes.Select(RecoveryCodes.Hash).ToList());
        }

        return new Staff(user, key!, codes);
    }

    private static object Describe(Staff staff, IAccessTokenGenerator tokens, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var step = Totp.StepAt(now);
        return new
        {
            userId = staff.User.Id,
            email = staff.User.Email,
            name = staff.User.Name,
            role = staff.User.Role.ToString(),
            password = StaffPassword,
            twoFactor = new
            {
                sharedKey = staff.Key,
                codes = new[] { Totp.Compute(staff.Key, step), Totp.Compute(staff.Key, step + 1) },
                secondsLeft = Totp.StepSeconds - (int)(now.ToUnixTimeSeconds() % Totp.StepSeconds),
                recoveryCodes = staff.RecoveryCodes,
            },
            accessToken = tokens.Create(staff.User, Guid.NewGuid()).Value,
        };
    }

    /// <summary>Текущий и следующий код 2FA служебного аккаунта разработки (для подтверждения действия, когда приложения-аутентификатора под рукой нет).</summary>
    private static async Task<IResult> StaffCodeAsync(HttpContext http, StaffCodeRequest? request, UserManager<User> users, TimeProvider clock)
    {
        http.Response.Headers.CacheControl = "no-store";
        var user = string.IsNullOrWhiteSpace(request?.Email) ? null : await users.FindByEmailAsync(request.Email.Trim());
        var key = user is { AccountType: AccountType.Staff } ? await users.GetAuthenticatorKeyAsync(user) : null;
        if (user is null || string.IsNullOrEmpty(key) || !user.TwoFactorEnabled)
        {
            return Problem(StatusCodes.Status404NotFound, "staff_not_found", "Служебный аккаунт с включённой 2FA не найден: сначала POST /dev/seed-staff.");
        }

        var now = clock.GetUtcNow();
        var step = Totp.StepAt(now);
        return Results.Ok(new
        {
            email = user.Email,
            codes = new[] { Totp.Compute(key, step), Totp.Compute(key, step + 1) },
            secondsLeft = Totp.StepSeconds - (int)(now.ToUnixTimeSeconds() % Totp.StepSeconds),
        });
    }

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

        var moderator = await EnsureStaffAsync(http, ModeratorEmail, "Модератор (dev)", UserRole.Moderator);
        var admin = await EnsureStaffAsync(http, AdminEmail, "Админ (dev)", UserRole.Admin);
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

        Staff? secondStaff = null;
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
            secondStaff = await EnsureStaffAsync(http, SecondModeratorEmail, "Модератор 2 (dev)", UserRole.Moderator);
            await admins.AssignReviewerAsync(admin.User.Id, caseId, new AssignReviewerRequest(secondStaff.User.Id), ct);
        }

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
            moderator = Describe(moderator, tokens, clock),
            admin = Describe(admin, tokens, clock),
            secondModerator = secondStaff is null ? null : Describe(secondStaff, tokens, clock),
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
