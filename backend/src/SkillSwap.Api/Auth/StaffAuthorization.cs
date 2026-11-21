using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Auth;

namespace SkillSwap.Api.Auth;

/// <summary>Названия политик доступа для служебных ролей (раздел 3 ТЗ).</summary>
public static class Policies
{
    /// <summary>Только модератор: очередь обращений, взятие в работу, чтение чата по обращению, решения.</summary>
    public const string Moderator = "Moderator";

    /// <summary>Админ или суперадмин: апелляции, запросы на бан, просмотр статусов обращений.</summary>
    public const string Admin = "Admin";

    /// <summary>Любая служебная роль (модератор, админ, суперадмин).</summary>
    public const string Staff = "Staff";

    /// <summary>Только суперадмин: системные настройки, журнал аудита всей системы, панель фоновых задач.</summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>
    /// Каждая служебная политика состоит из двух частей: роль в токене (быстрый отказ) и <see cref="ActiveStaffRequirement"/>: служебный аккаунт по базе действует
    /// (не снят и не заблокирован) и его роль в базе входит в разрешённые. Вторая часть не даёт снятому или разжалованному сотруднику пользоваться ещё живым токеном
    /// (в том числе читать чат и материалы обращений). Все маршруты /admin, /superadmin и /moderation обязаны использовать только эти политики, а не голую роль.
    /// </summary>
    public static void Register(AuthorizationOptions options)
    {
        Add(options, Moderator, UserRole.Moderator);
        Add(options, Admin, UserRole.Admin, UserRole.SuperAdmin);
        Add(options, Staff, UserRole.Moderator, UserRole.Admin, UserRole.SuperAdmin);
        Add(options, SuperAdmin, UserRole.SuperAdmin);
    }

    private static void Add(AuthorizationOptions options, string name, params UserRole[] roles) =>
        options.AddPolicy(name, p => p.RequireAuthenticatedUser().RequireRole(roles.Select(r => r.ToString())).AddRequirements(new ActiveStaffRequirement(roles)));
}

/// <summary>Служебный аккаунт действует по базе и его роль входит в <paramref name="Roles"/> (раздел 3 ТЗ). Проверяется на каждый запрос, а не только при входе.</summary>
public sealed record ActiveStaffRequirement(IReadOnlyCollection<UserRole> Roles) : IAuthorizationRequirement;

/// <summary>
/// Проверка <see cref="ActiveStaffRequirement"/>: одна на всех служебные маршруты. Токен со служебной ролью, но аккаунт по базе снят, заблокирован, удалён или роль изменилась: 403
/// <c>staff_not_active</c> (через общий обработчик ошибок). Токен с чужой ролью (обычный пользователь) обработчик не трогает: ему отказывает проверка роли.
/// </summary>
public sealed class ActiveStaffHandler : AuthorizationHandler<ActiveStaffRequirement>
{
    private readonly IProfileStore _profiles;

    public ActiveStaffHandler(IProfileStore profiles)
    {
        _profiles = profiles;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveStaffRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue("sub"), out var userId)
            || !Enum.TryParse<UserRole>(context.User.FindFirstValue(JwtAccessTokenGenerator.RoleClaim), out var claimed)
            || !requirement.Roles.Contains(claimed))
        {
            return;   // не служебная роль: откажет RequireRole
        }

        var user = await _profiles.FindUserAsync(userId, CancellationToken.None);
        if (user is null || user.IsRegular || user.Status != UserStatus.Active || !requirement.Roles.Contains(user.Role))
        {
            throw new ForbiddenException("staff_not_active", "Служебный аккаунт больше не действует.");
        }

        context.Succeed(requirement);
    }
}

/// <summary>
/// Ресурсная авторизация чата (раздел 10.2 ТЗ): ресурс — идентификатор обмена. Разрешено участникам обмена и модератору с активным доступом
/// (<see cref="ChatAccessPolicy"/>). Подбором Guid чужой чат не открыть: проверка идёт на каждый запрос.
/// </summary>
public sealed class ChatAccessRequirement : IAuthorizationRequirement
{
}

public sealed class ChatAccessHandler : AuthorizationHandler<ChatAccessRequirement, Guid>
{
    private readonly ChatAccessPolicy _policy;

    public ChatAccessHandler(ChatAccessPolicy policy)
    {
        _policy = policy;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ChatAccessRequirement requirement, Guid exchangeId)
    {
        if (!Guid.TryParse(context.User.FindFirstValue("sub"), out var userId)
            || !Enum.TryParse<UserRole>(context.User.FindFirstValue(JwtAccessTokenGenerator.RoleClaim), out var role))
        {
            return;
        }

        if (await _policy.CanReadAsync(userId, role, exchangeId, CancellationToken.None))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Адрес клиента из HTTP-запроса для журнала аудита.</summary>
public sealed class HttpRequestInfo : IRequestInfo
{
    private readonly IHttpContextAccessor _accessor;

    public HttpRequestInfo(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public string? IpAddress => _accessor.HttpContext?.Connection.RemoteIpAddress is { } ip
        ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString()
        : null;
}
