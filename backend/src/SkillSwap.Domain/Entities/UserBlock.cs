namespace SkillSwap.Domain.Entities;

/// <summary>
/// Пользователь заблокировал другого (таблица UserBlocks, раздел 13 ТЗ). Блокировка действует в обе стороны:
/// оба перестают видеть друг друга в подборе и профилях, а позже и не могут писать друг другу.
/// Ключ составной: кто заблокировал + кого.
/// </summary>
public class UserBlock
{
    /// <summary>Сколько пользователей можно заблокировать: защита таблицы от разрастания.</summary>
    public const int MaxPerUser = 200;

    private UserBlock()
    {
    }

    /// <summary>Кто заблокировал.</summary>
    public Guid UserId { get; private set; }

    /// <summary>Кого заблокировали.</summary>
    public Guid BlockedUserId { get; private set; }

    public DateTime CreatedAt { get; set; }

    public static UserBlock Create(Guid userId, Guid blockedUserId, DateTime createdAt)
    {
        if (userId == blockedUserId)
        {
            throw new Exceptions.DomainValidationException("cannot_block_self", "Нельзя заблокировать самого себя.");
        }

        return new UserBlock { UserId = userId, BlockedUserId = blockedUserId, CreatedAt = createdAt };
    }
}
