using SkillSwap.Application.Exceptions;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Files;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Dashboard;

/// <summary>
/// Главная страница кабинета (GET /me/dashboard): ближайший урок, счётчики, статистика, рейтинг и список того, что ждёт ответа. Один запрос
/// вместо пяти; доступен обычному аккаунту.
/// </summary>
public sealed class DashboardService
{
    /// <summary>Вход в комнату открывается за 10 минут до начала и закрывается через 30 минут после планового окончания (раздел 7.2 ТЗ).</summary>
    public static readonly TimeSpan JoinOpensBefore = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan JoinClosesAfter = TimeSpan.FromMinutes(30);

    private readonly IDashboardStore _store;
    private readonly IProfileStore _profiles;
    private readonly IExchangeStore _exchanges;
    private readonly AvatarUrls _avatars;
    private readonly TimeProvider _clock;

    public DashboardService(IDashboardStore store, IProfileStore profiles, IExchangeStore exchanges, AvatarUrls avatars, TimeProvider clock)
    {
        _store = store;
        _profiles = profiles;
        _exchanges = exchanges;
        _avatars = avatars;
        _clock = clock;
    }

    public async Task<DashboardDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Главная страница доступна только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var data = await _store.LoadAsync(userId, now, ct);

        NextSessionDto? next = null;
        if (data.NextSession is { } row)
        {
            var exchange = (await _exchanges.GetViewsAsync([row.ExchangeId], ct))[row.ExchangeId];
            var partner = exchange.UserA.Id == userId ? exchange.UserB : exchange.UserA;
            var urls = await _avatars.ForViewerAsync(userId, [(partner.Id, partner.AvatarUrl)], ct);
            var s = row.Session;
            next = new NextSessionDto(
                s.Id, s.ExchangeId, s.StartAt, s.EndAt, s.DurationMin, s.Topic, s.Status, s.TeacherId == userId,
                partner with { AvatarUrl = urls.GetValueOrDefault(partner.Id) }, s.StartAt - JoinOpensBefore, s.EndAt + JoinClosesAfter);
        }

        // Как в публичном профиле: у «Новичка» средней оценки нет, два отзыва ничего не говорят о человеке.
        var rating = new DashboardRatingDto(user.IsNewcomer ? null : user.RatingAvg, user.RatingCount, user.IsNewcomer);
        return new DashboardDto(next, data.Counters, data.Stats, rating, data.Actions);
    }
}
