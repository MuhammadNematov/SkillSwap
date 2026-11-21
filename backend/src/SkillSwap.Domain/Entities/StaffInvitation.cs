using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>Состояние приглашения (раздел 3 ТЗ), вычисляется по полям и времени.</summary>
public enum StaffInvitationStatus
{
    /// <summary>Отправлено, ссылка действует.</summary>
    Pending,

    /// <summary>Принято: создан служебный аккаунт.</summary>
    Accepted,

    /// <summary>Отозвано тем, кто пригласил, или другим админом.</summary>
    Revoked,

    /// <summary>Срок (72 часа) вышел, приглашение не принято.</summary>
    Expired,
}

/// <summary>
/// Приглашение стать модератором или админом (таблица StaffInvitations, раздел 3 ТЗ, правила 2 и 4). Ссылка из письма содержит случайный токен, в базе лежит только
/// его хеш. Срок 72 часа. По ссылке создаётся отдельный служебный аккаунт с обязательной 2FA; суперадмином по приглашению стать нельзя (он создаётся при развёртывании).
/// Модераторов приглашает админ или суперадмин, админов только суперадмин (это проверяет сервис, сущность следит за состоянием).
/// </summary>
public class StaffInvitation : BaseEntity
{
    /// <summary>Срок действия ссылки.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    public const int EmailMaxLength = 256;

    private StaffInvitation()
    {
    }

    /// <summary>Адрес в нижнем регистре, без пробелов: по нему ищут повторные приглашения.</summary>
    public string Email { get; private set; } = string.Empty;

    public UserRole TargetRole { get; private set; }

    /// <summary>Кто пригласил (служебный аккаунт).</summary>
    public Guid InvitedById { get; private set; }

    /// <summary>SHA-256 токена из ссылки (hex). Сам токен нигде не хранится.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public DateTime ExpiresAt { get; private set; }
    public DateTime? AcceptedAt { get; private set; }

    /// <summary>Служебный аккаунт, созданный по приглашению.</summary>
    public Guid? AcceptedUserId { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    /// <summary>Сколько раз ссылку отправляли заново (первая отправка не считается).</summary>
    public int ResendCount { get; private set; }

    public StaffInvitationStatus StatusAt(DateTime now)
    {
        if (AcceptedAt is not null)
        {
            return StaffInvitationStatus.Accepted;
        }

        if (RevokedAt is not null)
        {
            return StaffInvitationStatus.Revoked;
        }

        return now > ExpiresAt ? StaffInvitationStatus.Expired : StaffInvitationStatus.Pending;
    }

    public bool IsPending(DateTime now) => StatusAt(now) == StaffInvitationStatus.Pending;

    public static StaffInvitation Create(string? email, UserRole targetRole, Guid invitedById, string tokenHash, DateTime now)
    {
        if (targetRole is not (UserRole.Moderator or UserRole.Admin))
        {
            throw new DomainValidationException("invitation_role_invalid", "По приглашению можно стать только модератором или админом.");
        }

        var normalized = Normalize(email);
        if (normalized.Length == 0 || normalized.Length > EmailMaxLength || !normalized.Contains('@') || normalized.StartsWith('@') || normalized.EndsWith('@'))
        {
            throw new DomainValidationException("invitation_email_invalid", "Укажите корректный адрес почты.");
        }

        RequireHash(tokenHash);
        return new StaffInvitation
        {
            Email = normalized,
            TargetRole = targetRole,
            InvitedById = invitedById,
            TokenHash = tokenHash,
            ExpiresAt = now + Lifetime,
        };
    }

    public static string Normalize(string? email) => email?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>Принять: создан аккаунт <paramref name="userId"/>. Только пока приглашение действует.</summary>
    public void Accept(Guid userId, DateTime now)
    {
        RequirePending(now);
        AcceptedAt = now;
        AcceptedUserId = userId;
    }

    /// <summary>Отозвать действующее приглашение. Принятое отозвать нельзя; повторный отзыв и отзыв истёкшего ничего не меняют.</summary>
    /// <returns>true, если отозвано этим вызовом.</returns>
    public bool Revoke(DateTime now)
    {
        if (AcceptedAt is not null)
        {
            throw new InvalidStateTransitionException(nameof(StaffInvitation), nameof(StaffInvitationStatus.Accepted), nameof(StaffInvitationStatus.Revoked));
        }

        if (RevokedAt is not null || now > ExpiresAt)
        {
            return false;
        }

        RevokedAt = now;
        return true;
    }

    /// <summary>
    /// Отправить заново: новый токен и новый срок 72 часа; прежняя ссылка перестаёт действовать. Можно для действующего и истёкшего приглашения,
    /// нельзя для принятого и отозванного.
    /// </summary>
    public void Renew(string newTokenHash, DateTime now)
    {
        RequireHash(newTokenHash);
        var status = StatusAt(now);
        if (status is StaffInvitationStatus.Accepted or StaffInvitationStatus.Revoked)
        {
            throw new InvalidStateTransitionException(nameof(StaffInvitation), status.ToString(), nameof(StaffInvitationStatus.Pending));
        }

        TokenHash = newTokenHash;
        ExpiresAt = now + Lifetime;
        ResendCount++;
    }

    private void RequirePending(DateTime now)
    {
        switch (StatusAt(now))
        {
            case StaffInvitationStatus.Accepted:
                throw new DomainValidationException("invitation_already_used", "Это приглашение уже принято.");
            case StaffInvitationStatus.Revoked:
                throw new DomainValidationException("invitation_revoked", "Приглашение отозвано.");
            case StaffInvitationStatus.Expired:
                throw new DomainValidationException("invitation_expired", "Срок приглашения (72 часа) истёк: попросите прислать его заново.");
        }
    }

    private static void RequireHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64)
        {
            throw new DomainValidationException("invitation_token_invalid", "Хеш токена приглашения: 64 символа (SHA-256 в hex).");
        }
    }
}
