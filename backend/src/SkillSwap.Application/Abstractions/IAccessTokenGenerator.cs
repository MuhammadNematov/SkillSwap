using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Abstractions;

public interface IAccessTokenGenerator
{
    /// <summary>
    /// Выпускает короткоживущий access JWT для пользователя. <paramref name="sessionId"/> кладётся в claim sid:
    /// по нему сервер понимает, какой сеанс «текущий» (список сеансов, «выйти на остальных устройствах»).
    /// </summary>
    AccessToken Create(User user, Guid sessionId);
}

public sealed record AccessToken(string Value, DateTime ExpiresAt);
