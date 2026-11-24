using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Options;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Api.DevTools;

/// <summary>
/// Данные для ручной проверки панелей админа и суперадмина (только Development): <c>POST /dev/seed-admin</c>. Создаёт (при первом вызове) весь служебный состав
/// (модераторы, админ и суперадмин с известными паролем и ключом 2FA, как в <c>/dev/seed-staff</c>) и **при каждом вызове** новые данные для экранов этапа 7:
/// три личных пользователя (для блокировки и разблокировки), два одинаковых и одно другое предложение навыка (разбор «вместе»), запрос на бан, действующее приглашение
/// модератора с готовой ссылкой, запрос на привязку личного аккаунта к служебному с готовой ссылкой, **новый админ для снятия** (<c>removableAdmin</c>: суперадмин проверяет «Снять админа»,
/// не теряя постоянного <c>dev-admin</c>) и **настоящее предупреждение** пользователю B (<c>warning</c>: «Снять предупреждение» в карточке пользователя). Просроченные обращения, статистика и журнал аудита наполняются сами:
/// обращения создаёт <c>POST /dev/seed-case</c> (сроки сокращены в <c>appsettings.Development.json</c>), а каждое действие админа пишет аудит.
/// Суперадмин нужен для панели Hangfire и системных настроек: <c>POST /superadmin/hangfire/session</c> принимает его токен и подтверждение (пароль и код 2FA).
/// </summary>
public static class AdminSeedDevTools
{
    public static IEndpointRouteBuilder MapAdminSeedDevTools(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/seed-admin", SeedAdminAsync).ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> SeedAdminAsync(
        HttpContext http, AppDbContext db, UserManager<User> users, IAccessTokenGenerator tokens, TimeProvider clock, IOptions<FrontendOptions> frontend,
        ExchangeRequestService requests, ChatService chat, CaseService cases, ModerationService moderation, ResolutionService resolution, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var skills = await db.Skills.AsNoTracking().Where(s => s.IsActive && s.Category!.IsActive).OrderBy(s => s.NameEn).Select(s => s.Id).Take(2).ToListAsync(ct);
        var category = await db.SkillCategories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (skills.Count < 2 || category is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "В каталоге меньше двух навыков: сначала нужен стартовый каталог.",
                extensions: new Dictionary<string, object?> { ["code"] = "skill_catalog_empty" });
        }

        var moderator = await CaseSeedDevTools.EnsureStaffAsync(http, CaseSeedDevTools.ModeratorEmail, "Модератор (dev)", UserRole.Moderator);
        var second = await CaseSeedDevTools.EnsureStaffAsync(http, CaseSeedDevTools.SecondModeratorEmail, "Модератор 2 (dev)", UserRole.Moderator);
        var admin = await CaseSeedDevTools.EnsureStaffAsync(http, CaseSeedDevTools.AdminEmail, "Админ (dev)", UserRole.Admin);
        var superAdmin = await CaseSeedDevTools.EnsureStaffAsync(http, CaseSeedDevTools.SuperAdminEmail, "Суперадмин (dev)", UserRole.SuperAdmin);

        var now = clock.GetUtcNow().UtcDateTime;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        // Новый админ при каждом вызове: его можно снять, а постоянный dev-admin остаётся для входа
        var removableAdmin = await CaseSeedDevTools.EnsureStaffAsync(http, $"dev-admin-removable-{suffix}@example.com", "Админ для снятия (dev)", UserRole.Admin);
        var first = await LessonSeedDevTools.CreateUserAsync(users, db, $"dev-user-a-{suffix}@example.com", "Пользователь A (dev)", skills[0], skills[1], ct);
        var other = await LessonSeedDevTools.CreateUserAsync(users, db, $"dev-user-b-{suffix}@example.com", "Пользователь B (dev)", skills[1], skills[0], ct);
        var third = await LessonSeedDevTools.CreateUserAsync(users, db, $"dev-user-c-{suffix}@example.com", "Пользователь C (dev)", skills[0], skills[1], ct);

        // Настоящее предупреждение пользователю B (его выдал модератор по обращению пользователя A): кнопка «Снять» в карточке пользователя
        var warningCaseId = await CaseSeedDevTools.IssueWarningAsync(requests, chat, cases, moderation, resolution, moderator.User.Id, first.User.Id, other.User.Id, skills[0], skills[1], ct);

        // Два одинаковых предложения от разных авторов (разбираются вместе) и одно другое.
        var duplicateName = $"Dev Навык {suffix}";
        db.SkillSuggestions.Add(SkillSuggestion.Create(first.User.Id, category.Value, duplicateName));
        db.SkillSuggestions.Add(SkillSuggestion.Create(other.User.Id, category.Value, duplicateName));
        db.SkillSuggestions.Add(SkillSuggestion.Create(third.User.Id, category.Value, $"Dev Другой навык {suffix}"));

        // Запрос на бан от системы: админ одобряет (общая блокировка) или отклоняет.
        var ban = BanRequest.Automatic(third.User.Id, null, "Запрос на бан для проверки экрана админа (данные разработки).");
        db.BanRequests.Add(ban);

        // Приглашение модератора с известным токеном: открыть ссылку и принять без почты.
        var (invitationToken, invitationHash) = StaffInvitationTokens.Generate();
        var invitation = StaffInvitation.Create($"dev-invitee-{suffix}@example.com", UserRole.Moderator, admin.User.Id, invitationHash, now);
        db.StaffInvitations.Add(invitation);

        // Запрос на привязку: второй модератор просит привязать личного пользователя; прежние ожидающие запросы этого сотрудника отменяются (как в настоящем сервисе).
        var link = await CreateLinkRequestAsync(db, second.User.Id, other.User.Id, now, ct);
        await db.SaveChangesAsync(ct);

        var warningId = await db.Warnings.AsNoTracking().Where(w => w.CaseId == warningCaseId && w.UserId == other.User.Id).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);

        var baseUrl = frontend.Value;
        return Results.Ok(new
        {
            superAdmin = CaseSeedDevTools.Describe(superAdmin, tokens, clock),
            admin = CaseSeedDevTools.Describe(admin, tokens, clock),
            removableAdmin = CaseSeedDevTools.Describe(removableAdmin, tokens, clock),
            moderator = CaseSeedDevTools.Describe(moderator, tokens, clock),
            secondModerator = CaseSeedDevTools.Describe(second, tokens, clock),
            users = new[] { Regular("userA", first, tokens), Regular("userB", other, tokens), Regular("userC", third, tokens) },
            warning = new { id = warningId, userId = other.User.Id, userEmail = other.User.Email, caseId = warningCaseId, note = "Действующее предупреждение пользователя B: Админка → Пользователи → B → «Снять»." },
            skillSuggestions = new { duplicateName, count = 3 },
            banRequestId = ban.Id,
            banRequestUserId = third.User.Id,
            invitation = new { id = invitation.Id, email = invitation.Email, role = invitation.TargetRole.ToString(), url = baseUrl.StaffInvitationUrl(invitationToken), expiresAt = invitation.ExpiresAt },
            linkRequest = new
            {
                id = link.Id,
                staffId = second.User.Id,
                personalUserId = other.User.Id,
                personalEmail = other.User.Email,
                url = baseUrl.PersonalLinkUrl(link.Token),
                note = "Ссылку открывает владелец личного аккаунта (userB) после входа: пароль userB в users.",
            },
            hint = "Суперадмин (панель Hangfire, настройки, снятие админов): токен в superAdmin.accessToken, пароль и коды 2FA там же. Просроченные обращения и данные статистики: POST /dev/seed-case "
                + "(сроки сокращены в appsettings.Development.json). Маршрут можно вызывать повторно: каждый раз создаются новые пользователи, предложения, запрос на бан, приглашение, запрос на привязку, админ для снятия и предупреждение.",
        });
    }

    private static async Task<(Guid Id, string Token)> CreateLinkRequestAsync(AppDbContext db, Guid staffId, Guid personalId, DateTime now, CancellationToken ct)
    {
        foreach (var old in await db.PersonalLinkRequests.Where(r => r.StaffId == staffId && r.ConfirmedAt == null && r.DeclinedAt == null && r.CancelledAt == null).ToListAsync(ct))
        {
            old.Cancel(now);
        }

        var (token, hash) = StaffInvitationTokens.Generate();
        var request = PersonalLinkRequest.Create(staffId, personalId, hash, now);
        db.PersonalLinkRequests.Add(request);
        return (request.Id, token);
    }

    private static object Regular(string label, LessonSeedDevTools.Seeded seeded, IAccessTokenGenerator tokens) => new
    {
        label,
        userId = seeded.User.Id,
        email = seeded.User.Email,
        password = seeded.Password,
        name = seeded.User.Name,
        accessToken = tokens.Create(seeded.User, Guid.NewGuid()).Value,
    };
}
