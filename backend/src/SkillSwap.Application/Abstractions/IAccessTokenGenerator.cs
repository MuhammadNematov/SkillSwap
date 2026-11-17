using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Abstractions;

public interface IAccessTokenGenerator
{
    /// <summary>Выпускает короткоживущий access JWT для пользователя.</summary>
    AccessToken Create(User user);
}

public sealed record AccessToken(string Value, DateTime ExpiresAt);
