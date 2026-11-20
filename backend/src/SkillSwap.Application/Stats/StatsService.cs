using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Stats;

/// <param name="From">Начало периода (включительно, UTC).</param>
/// <param name="To">Конец периода (не включая, UTC).</param>
public sealed record StatsPeriodDto(DateTime From, DateTime To);

/// <param name="Active">Действующих обычных аккаунтов сейчас.</param>
/// <param name="NewInPeriod">Зарегистрировано за период.</param>
/// <param name="ActiveInPeriod">Заходили за период (по отметке последней активности: она обновляется не на каждый запрос, цифра приблизительная).</param>
public sealed record UserStatsDto(int Active, int Blocked, int Deleted, int NewInPeriod, int ActiveInPeriod, int Moderators, int Admins, int SuperAdmins);

/// <param name="Active">Активных обменов сейчас.</param>
/// <param name="Frozen">Замороженных (по жалобе) сейчас.</param>
public sealed record ExchangeStatsDto(int Active, int Frozen, int CreatedInPeriod, int CompletedInPeriod, int AbortedInPeriod, int AbortedForNoShowInPeriod);

/// <summary>Уроки, плановое время начала которых попало в период, по итогу.</summary>
/// <param name="Completed">Проведено.</param>
/// <param name="NoShow">Неявка одного из участников.</param>
/// <param name="Missed">Не состоялся: никто не пришёл.</param>
/// <param name="Cancelled">Отменён (в том числе поздно).</param>
/// <param name="Unconfirmed">Время не подтверждено сторонами или урок не засчитан.</param>
public sealed record LessonStatsDto(int Completed, int NoShow, int Missed, int Cancelled, int Unconfirmed);

/// <summary>Время реакции: от подачи обращения до первого взятия модератором.</summary>
/// <param name="CasesTaken">По скольким обращениям считается (по ним уже была реакция).</param>
/// <param name="AverageHours">Среднее в часах; null, если данных нет.</param>
/// <param name="MedianHours">Медиана в часах; null, если данных нет.</param>
/// <param name="WithinSlaPercent">Доля обращений, взятых до конца срока реакции (24 часа высокий приоритет, 72 обычный), в процентах; null, если данных нет.</param>
public sealed record ReactionStatsDto(int CasesTaken, double? AverageHours, double? MedianHours, double? WithinSlaPercent);

/// <param name="OpenedByType">Подано за период по типам (все типы, нули тоже).</param>
/// <param name="ResolvedByDecision">Решено за период по решениям (все решения, нули тоже).</param>
/// <param name="Appealed">Апелляций подано за период.</param>
/// <param name="Backlog">Сейчас по статусам (все статусы, нули тоже).</param>
/// <param name="OverdueNow">Сейчас ждут взятия дольше срока реакции.</param>
/// <param name="OverdueMarkedInPeriod">Сколько обращений за период признано просроченными (срок реакции вышел, а их не взяли).</param>
/// <param name="Reaction">По обращениям, поданным за период.</param>
public sealed record CaseStatsDto(
    int Opened, IReadOnlyDictionary<string, int> OpenedByType, int Resolved, IReadOnlyDictionary<string, int> ResolvedByDecision, int Closed, int Appealed,
    IReadOnlyDictionary<string, int> Backlog, int OverdueNow, int OverdueMarkedInPeriod, ReactionStatsDto Reaction);

public sealed record AdminStatsDto(StatsPeriodDto Period, UserStatsDto Users, ExchangeStatsDto Exchanges, LessonStatsDto Lessons, CaseStatsDto Cases);

/// <param name="Taken">Сколько раз взял обращения в работу за период.</param>
/// <param name="Resolved">Решено за период.</param>
/// <param name="AppealReviewsConcluded">Заключений по апелляциям за период.</param>
/// <param name="InWorkNow">В работе сейчас (в работе и ждут ответов сторон).</param>
/// <param name="Reaction">По обращениям, которые он взял первым за период.</param>
public sealed record ModeratorStatsDto(
    StatsPeriodDto Period, int Taken, int Resolved, IReadOnlyDictionary<string, int> ResolvedByDecision, int AppealReviewsConcluded, int InWorkNow, ReactionStatsDto Reaction);

/// <summary>Одно наблюдение времени реакции: сколько секунд прошло до первого взятия и уложились ли в срок.</summary>
public sealed record ReactionSample(double Seconds, bool WithinSla);

/// <summary>Агрегаты по обращениям модератора за период (без самих обращений).</summary>
public sealed record ModeratorCounts(int Taken, int Resolved, IReadOnlyDictionary<string, int> ResolvedByDecision, int AppealReviewsConcluded, int InWorkNow);

/// <summary>Агрегатные выборки для статистики: только счётчики, без персональных данных и без списков людей.</summary>
public interface IStatsStore
{
    Task<UserStatsDto> UsersAsync(DateTime from, DateTime to, CancellationToken ct);

    Task<ExchangeStatsDto> ExchangesAsync(DateTime from, DateTime to, CancellationToken ct);

    Task<LessonStatsDto> LessonsAsync(DateTime from, DateTime to, CancellationToken ct);

    /// <summary>Счётчики по обращениям; <see cref="CaseStatsDto.Reaction"/> заполняет сервис.</summary>
    Task<CaseStatsDto> CasesAsync(DateTime from, DateTime to, DateTime now, CancellationToken ct);

    /// <summary>Время реакции по обращениям, поданным за период и уже взятым.</summary>
    Task<IReadOnlyList<ReactionSample>> ReactionSamplesAsync(DateTime from, DateTime to, CancellationToken ct);

    Task<ModeratorCounts> ModeratorCountsAsync(Guid moderatorId, DateTime from, DateTime to, CancellationToken ct);

    /// <summary>Время реакции по обращениям, которые этот модератор взял первым за период.</summary>
    Task<IReadOnlyList<ReactionSample>> ModeratorReactionSamplesAsync(Guid moderatorId, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>
/// Статистика для панелей (раздел 3 ТЗ): полная для админа и суперадмина, личная для модератора. Только агрегаты, без имён, адресов и идентификаторов людей и обращений. Период по умолчанию
/// последние 30 дней, не длиннее 366 дней. Считается из базы при каждом запросе (кэша нет).
/// </summary>
public sealed class StatsService
{
    public const int DefaultDays = 30;
    public const int MaxDays = 366;

    private readonly IStatsStore _store;
    private readonly IProfileStore _profiles;
    private readonly TimeProvider _clock;

    public StatsService(IStatsStore store, IProfileStore profiles, TimeProvider clock)
    {
        _store = store;
        _profiles = profiles;
        _clock = clock;
    }

    /// <summary>Полная статистика платформы. 400 <c>invalid_period</c>, <c>period_too_long</c>; 403 для не админа.</summary>
    public async Task<AdminStatsDto> GetAdminStatsAsync(Guid adminId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var (start, end, now) = Period(from, to);
        var user = await _profiles.FindUserAsync(adminId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role is not (UserRole.Admin or UserRole.SuperAdmin) || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("admin_required", "Доступно только администратору.");
        }

        var cases = await _store.CasesAsync(start, end, now, ct);
        return new AdminStatsDto(
            new StatsPeriodDto(start, end), await _store.UsersAsync(start, end, ct), await _store.ExchangesAsync(start, end, ct), await _store.LessonsAsync(start, end, ct),
            cases with { Reaction = Summarize(await _store.ReactionSamplesAsync(start, end, ct)) });
    }

    /// <summary>Личная статистика модератора (только его работа). 400 <c>invalid_period</c>, <c>period_too_long</c>; 403 для не модератора.</summary>
    public async Task<ModeratorStatsDto> GetModeratorStatsAsync(Guid moderatorId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var (start, end, _) = Period(from, to);
        var user = await _profiles.FindUserAsync(moderatorId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role != UserRole.Moderator || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("moderator_required", "Доступно только модератору.");
        }

        var counts = await _store.ModeratorCountsAsync(moderatorId, start, end, ct);
        return new ModeratorStatsDto(
            new StatsPeriodDto(start, end), counts.Taken, counts.Resolved, counts.ResolvedByDecision, counts.AppealReviewsConcluded, counts.InWorkNow,
            Summarize(await _store.ModeratorReactionSamplesAsync(moderatorId, start, end, ct)));
    }

    /// <summary>Сводка наблюдений: среднее и медиана в часах (отрицательное время, возможное из-за расхождения часов, считается нулём), доля уложившихся в срок.</summary>
    public static ReactionStatsDto Summarize(IReadOnlyList<ReactionSample> samples)
    {
        if (samples.Count == 0)
        {
            return new ReactionStatsDto(0, null, null, null);
        }

        var hours = samples.Select(s => Math.Max(0, s.Seconds) / 3600.0).Order().ToList();
        var middle = hours.Count / 2;
        var median = hours.Count % 2 == 1 ? hours[middle] : (hours[middle - 1] + hours[middle]) / 2;
        return new ReactionStatsDto(
            samples.Count, Math.Round(hours.Average(), 2), Math.Round(median, 2), Math.Round(100.0 * samples.Count(s => s.WithinSla) / samples.Count, 1));
    }

    private (DateTime From, DateTime To, DateTime Now) Period(DateTime? from, DateTime? to)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var end = AsUtc(to) ?? now;
        var start = AsUtc(from) ?? end.AddDays(-DefaultDays);
        if (start >= end)
        {
            throw new BadRequestException("invalid_period", "Начало периода должно быть раньше конца.");
        }

        if (end - start > TimeSpan.FromDays(MaxDays))
        {
            throw new BadRequestException("period_too_long", $"Период не длиннее {MaxDays} дней.");
        }

        return (start, end, now);
    }

    private static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        var v => DateTime.SpecifyKind(v.Value, DateTimeKind.Utc),
    };
}
