using Microsoft.AspNetCore.Identity;
using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Пользователь (таблица Users, раздел 13 ТЗ). Поля Email, PasswordHash, Lockout, 2FA и т. п.
/// приходят из Identity; здесь только наши.
/// </summary>
public class User : IdentityUser<Guid>, ITimestamped
{
    /// <summary>Минимальный возраст регистрации (FR-3).</summary>
    public const int MinAge = 16;

    /// <summary>Младше этого возраста действуют правила защиты подростков (раздел 10.4).</summary>
    public const int AdultAge = 18;

    public const string DefaultUiLanguage = "en";
    public const string DefaultTimeZone = "UTC";

    public string Name { get; private set; } = string.Empty;
    /// <summary>Ключ аватара в хранилище файлов (null — аватара нет). Наружу отдаётся не он, а подписанная ссылка на час (AvatarUrls).</summary>
    public string? AvatarKey { get; set; }
    public string? Bio { get; set; }
    /// <summary>Обязательна у обычных пользователей; у служебных аккаунтов анкеты нет (раздел 3 ТЗ).</summary>
    public DateOnly? DateOfBirth { get; private set; }
    public AccountType AccountType { get; private set; } = AccountType.Regular;
    public UserRole Role { get; private set; } = UserRole.User;
    public string UiLanguage { get; set; } = DefaultUiLanguage;

    /// <summary>Часовой пояс IANA, например Asia/Dushanbe. Уточняется в анкете.</summary>
    public string TimeZone { get; set; } = DefaultTimeZone;

    public bool IsSearchable { get; set; }

    /// <summary>
    /// Пользователь сам выключил показ в подборе (был виден и нажал «скрыть»). Пока это так, сервер не включает видимость
    /// автоматически после заполнения анкеты. Снова показаться пользователь может только сам.
    /// </summary>
    public bool HiddenByUser { get; private set; }

    /// <summary>Когда пользователь удалил аккаунт (Status = Deleted); данные при этом обезличиваются, см. <see cref="MarkDeleted"/>.</summary>
    public DateTime? DeletedAt { get; private set; }

    /// <summary>
    /// Аккаунт удалён, но личные данные (имя, био, дата рождения, навыки) пока сохранены: по пользователю есть открытое обращение (раздел 8).
    /// Данные стираются, когда оно закрыто.
    /// </summary>
    public bool ErasurePending { get; private set; }

    /// <summary>
    /// Хеш шестизначного кода подтверждения email (вместе со ссылкой из письма), срок его действия и сколько раз его уже пробовали ввести.
    /// Меняются только атомарными запросами хранилища кодов (<c>IEmailCodeStore</c>): число попыток не должно теряться при параллельных запросах.
    /// </summary>
    public string? EmailCodeHash { get; private set; }

    public DateTime? EmailCodeExpiresAt { get; private set; }
    public int EmailCodeAttempts { get; private set; }
    public UserStatus Status { get; private set; } = UserStatus.Active;
    public Guid? LinkedUserId { get; private set; }
    public decimal RatingAvg { get; private set; }
    public int RatingCount { get; private set; }
    public DateTime? LastActiveAt { get; set; }

    /// <summary>
    /// Последний использованный временной шаг TOTP. Код принимается, только если его шаг строго больше:
    /// так один и тот же код нельзя использовать дважды. Меняется только атомарным UPDATE в хранилище шагов.
    /// </summary>
    public long? TwoFactorLastUsedStep { get; private set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Создаёт обычного пользователя при регистрации. Подбор откроется после заполнения анкеты.</summary>
    public static User CreateRegular(string email, string name, DateOnly dateOfBirth, string uiLanguage)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            Name = name,
            DateOfBirth = dateOfBirth,
            UiLanguage = uiLanguage,
            AccountType = AccountType.Regular,
            Role = UserRole.User,
            Status = UserStatus.Active,
            IsSearchable = false,
        };
    }

    /// <summary>
    /// Создаёт служебный аккаунт (модератор, админ, суперадмин): без анкеты, подбора и рейтинга.
    /// 2FA у него ещё не настроена; войти без неё он не сможет, при первом входе её нужно включить.
    /// </summary>
    public static User CreateStaff(string email, string name, UserRole role)
    {
        if (role == UserRole.User)
        {
            throw new ArgumentException("Служебный аккаунт не может иметь роль User.", nameof(role));
        }

        return new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            Name = name,
            UiLanguage = DefaultUiLanguage,
            AccountType = AccountType.Staff,
            Role = role,
            Status = UserStatus.Active,
            IsSearchable = false,
        };
    }

    /// <summary>
    /// Привязывает к служебному аккаунту личный аккаунт сотрудника (раздел 3 ТЗ): по нему проверяется конфликт интересов, модератор не берёт обращения, где участвует его
    /// личный аккаунт. Привязать можно только обычный аккаунт, не больше одного служебного на личный (уникальный индекс).
    /// </summary>
    public void LinkPersonalAccount(User personal)
    {
        if (IsRegular)
        {
            throw new DomainValidationException("linked_account_staff_only", "Личный аккаунт привязывается к служебному, а не к обычному.");
        }

        if (!personal.IsRegular)
        {
            throw new DomainValidationException("linked_account_must_be_regular", "Привязывается обычный аккаунт.");
        }

        LinkedUserId = personal.Id;
    }

    /// <summary>Полных лет на указанную дату (UTC); null, если даты рождения нет (служебный аккаунт).</summary>
    public int? GetAge(DateOnly today)
    {
        if (DateOfBirth is not { } birth)
        {
            return null;
        }

        var age = today.Year - birth.Year;
        if (birth > today.AddYears(-age))
        {
            age--;
        }

        return age;
    }

    public bool IsMinor(DateOnly today) => GetAge(today) is { } age && age < AdultAge;

    /// <summary>Пока отзывов меньше этого числа, профиль показывается как «Новичок» без средней оценки (раздел 8.1 ТЗ).</summary>
    public const int NewcomerReviewThreshold = 3;

    public const int NameMaxLength = 100;
    public const int BioMaxLength = 1000;

    public bool IsNewcomer => RatingCount < NewcomerReviewThreshold;

    /// <summary>Есть анкета, подбор и рейтинг только у обычных аккаунтов (раздел 3 ТЗ).</summary>
    public bool IsRegular => AccountType == AccountType.Regular;

    /// <summary>
    /// Меняет то, что пользователь правит в профиле. Всё проверяется до записи: при ошибке ничего не меняется.
    /// Язык и часовой пояс проверяет сервис (список языков и поясов живёт там).
    /// </summary>
    public void UpdateProfile(string name, string? bio, string uiLanguage, string timeZone, bool isSearchable)
    {
        if (!IsRegular)
        {
            throw new Exceptions.DomainValidationException("staff_has_no_profile", "У служебного аккаунта нет профиля.");
        }

        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length == 0 || trimmedName.Length > NameMaxLength)
        {
            throw new Exceptions.DomainValidationException("invalid_name", $"Имя: от 1 до {NameMaxLength} символов.");
        }

        var trimmedBio = string.IsNullOrWhiteSpace(bio) ? null : bio.Trim();
        if (trimmedBio is { Length: > BioMaxLength })
        {
            throw new Exceptions.DomainValidationException("invalid_bio", $"О себе: не больше {BioMaxLength} символов.");
        }

        Name = trimmedName;
        Bio = trimmedBio;
        UiLanguage = uiLanguage;
        TimeZone = timeZone;
        // Запоминаем волю пользователя: «выключил сам» (был виден и скрылся) и «включил сам» (снял прежнее решение).
        if (IsSearchable && !isSearchable)
        {
            HiddenByUser = true;
        }
        else if (isSearchable)
        {
            HiddenByUser = false;
        }

        IsSearchable = isSearchable;
    }

    public bool IsBlocked => Status == UserStatus.Blocked;

    /// <summary>Админ одобрил запрос на блокировку (раздел 8.3 ТЗ): Active → Blocked. Из подбора пользователь исчезает, войти не может. Служебный аккаунт так не блокируется.</summary>
    public void Block()
    {
        if (!IsRegular)
        {
            throw new Exceptions.DomainValidationException("staff_cannot_be_blocked", "Служебный аккаунт блокируется отдельно.");
        }

        if (Status != UserStatus.Active)
        {
            throw new Exceptions.DomainValidationException("user_not_active", "Заблокировать можно только действующий аккаунт.");
        }

        Status = UserStatus.Blocked;
    }

    /// <summary>Подпись удалённого пользователя в чатах, заявках и отзывах (раздел 8.1 ТЗ); клиент по флагу <c>isDeleted</c> показывает её на своём языке.</summary>
    public const string DeletedDisplayName = "Удалённый пользователь";

    /// <summary>
    /// Пользователь удалил аккаунт: статус Deleted, из подбора он исчезает навсегда. Личные данные стираются здесь же
    /// (<paramref name="keepPersonalData"/> = false) или остаются до закрытия обращения (<c>true</c>, <see cref="ErasurePending"/>).
    /// Адрес почты, пароль и 2FA убирает сервис (через Identity).
    /// </summary>
    public void MarkDeleted(DateTime now, bool keepPersonalData)
    {
        if (!IsRegular)
        {
            throw new Exceptions.DomainValidationException("staff_cannot_delete_account", "Служебный аккаунт удалить нельзя.");
        }

        Status = UserStatus.Deleted;
        DeletedAt ??= now;
        IsSearchable = false;
        if (keepPersonalData)
        {
            ErasurePending = true;
            return;
        }

        WipePersonalData();
    }

    /// <summary>Стирает личные данные: имя, био, дату рождения, аватар. Вызывается при удалении и после закрытия обращения.</summary>
    public void WipePersonalData()
    {
        Name = DeletedDisplayName;
        Bio = null;
        DateOfBirth = null;
        AvatarKey = null;
        TimeZone = DefaultTimeZone;
        UiLanguage = DefaultUiLanguage;
        ErasurePending = false;
    }
}
