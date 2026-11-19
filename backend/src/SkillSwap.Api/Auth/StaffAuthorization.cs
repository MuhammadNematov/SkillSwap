using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SkillSwap.Application.Moderation;
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

    public static void Register(AuthorizationOptions options)
    {
        options.AddPolicy(Moderator, p => p.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Moderator)));
        options.AddPolicy(Admin, p => p.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Admin), nameof(UserRole.SuperAdmin)));
        options.AddPolicy(Staff, p => p.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Moderator), nameof(UserRole.Admin), nameof(UserRole.SuperAdmin)));
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
