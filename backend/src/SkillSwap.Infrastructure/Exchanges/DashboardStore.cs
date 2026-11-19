using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Dashboard;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Exchanges;

/// <summary>Данные главной страницы: несколько небольших запросов по индексам (обмены и уроки пользователя), без загрузки списков целиком.</summary>
public sealed class DashboardStore : IDashboardStore
{
    /// <summary>Сколько элементов каждого вида показывается в «Что ждёт ответа»: список не должен разрастаться.</summary>
    private const int ActionsPerKind = 20;

    private readonly AppDbContext _db;

    public DashboardStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<DashboardData> LoadAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var mine = _db.Exchanges.AsNoTracking().Where(e => e.UserAId == userId || e.UserBId == userId);

        var exchangeCounts = await mine.GroupBy(e => e.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        var active = exchangeCounts.Where(c => c.Status is ExchangeStatus.Active or ExchangeStatus.Frozen).Sum(c => c.Count);
        var completedExchanges = exchangeCounts.Where(c => c.Status == ExchangeStatus.Completed).Sum(c => c.Count);

        var incoming = await _db.ExchangeRequests.CountAsync(r => r.ToUserId == userId && r.Status == ExchangeRequestStatus.Pending && r.ExpiresAt > now, ct);
        var outgoing = await _db.ExchangeRequests.CountAsync(r => r.FromUserId == userId && r.Status == ExchangeRequestStatus.Pending && r.ExpiresAt > now, ct);
        var unreadNotifications = await _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead, ct);

        // Чаты, которые ещё доступны для чтения: активные, замороженные и закрытые не больше 30 дней назад.
        var readableSince = now.AddDays(-ChatAccess.ReadOnlyDaysAfterClose);
        var unreadMessages = await (from m in _db.Messages.AsNoTracking()
                                    join e in mine on m.ExchangeId equals e.Id
                                    where m.SenderId != userId && m.ReadAt == null && m.DeletedAt == null
                                          && (e.Status == ExchangeStatus.Active || e.Status == ExchangeStatus.Frozen || e.ClosedAt > readableSince)
                                    select m.Id).CountAsync(ct);

        var completed = await (from s in _db.Sessions.AsNoTracking()
                               join e in mine on s.ExchangeId equals e.Id
                               where s.Status == SessionStatus.Completed
                               select new { s.TeacherId, s.DurationMin }).ToListAsync(ct);
        var stats = new DashboardStatsDto(
            completed.Count,
            completed.Where(c => c.TeacherId == userId).Sum(c => c.DurationMin),
            completed.Where(c => c.TeacherId != userId).Sum(c => c.DurationMin),
            completedExchanges);

        var next = await (from s in _db.Sessions.AsNoTracking()
                          join e in mine on s.ExchangeId equals e.Id
                          where (s.Status == SessionStatus.Scheduled || s.Status == SessionStatus.InProgress) && s.EndAt > now
                          orderby s.StartAt, s.Id
                          select new { Session = s, e.UserAId, e.UserBId }).FirstOrDefaultAsync(ct);

        var actions = await ActionsAsync(userId, now, mine, ct);

        return new DashboardData(
            next is null ? null : new NextSessionRow(next.Session, next.Session.ExchangeId, next.UserAId == userId ? next.UserBId : next.UserAId),
            new DashboardCountersDto(active, incoming, outgoing, unreadMessages, unreadNotifications),
            stats,
            actions);
    }

    private async Task<List<ActionNeededDto>> ActionsAsync(Guid userId, DateTime now, IQueryable<Exchange> mine, CancellationToken ct)
    {
        var actions = new List<(DateTime Due, ActionNeededDto Dto)>();

        foreach (var r in await _db.ExchangeRequests.AsNoTracking()
                     .Where(r => r.ToUserId == userId && r.Status == ExchangeRequestStatus.Pending && r.ExpiresAt > now)
                     .OrderBy(r => r.ExpiresAt).Take(ActionsPerKind).Select(r => new { r.Id, r.ExpiresAt }).ToListAsync(ct))
        {
            actions.Add((r.ExpiresAt, new ActionNeededDto(ActionTypes.AnswerRequest, null, null, r.Id, r.ExpiresAt)));
        }

        // Предложенное время, на которое должен ответить я (его предложил партнёр), пока урок не начался.
        foreach (var s in await (from s in _db.Sessions.AsNoTracking()
                                 join e in mine on s.ExchangeId equals e.Id
                                 where s.Status == SessionStatus.Proposed && s.ProposedById != userId && s.StartAt > now && e.Status == ExchangeStatus.Active
                                 orderby s.StartAt
                                 select new { s.Id, s.ExchangeId, s.StartAt }).Take(ActionsPerKind).ToListAsync(ct))
        {
            actions.Add((s.StartAt, new ActionNeededDto(ActionTypes.AnswerSession, s.Id, s.ExchangeId, null, s.StartAt)));
        }

        // «Урок состоялся»: урок закончился, я ещё не нажал, срок подтверждения не вышел.
        foreach (var s in await (from s in _db.Sessions.AsNoTracking()
                                 join e in mine on s.ExchangeId equals e.Id
                                 where s.Status == SessionStatus.Unconfirmed && s.UnconfirmedAt != null
                                       && ((e.UserAId == userId && !s.ConfirmedByA) || (e.UserBId == userId && !s.ConfirmedByB))
                                 select new { s.Id, s.ExchangeId, s.UnconfirmedAt })
                     .ToListAsync(ct))
        {
            var deadline = s.UnconfirmedAt!.Value + LessonSession.ManualConfirmWindow;
            if (deadline > now)
            {
                actions.Add((deadline, new ActionNeededDto(ActionTypes.ConfirmSession, s.Id, s.ExchangeId, null, deadline)));
            }
        }

        // Партнёр нажал «Завершить» или согласился продлить, а я ещё нет.
        foreach (var e in await mine.Where(e => e.Status == ExchangeStatus.Active).Select(e => new
        {
            e.Id, e.EndDate, e.IsExtended,
            PartnerFinished = e.UserAId == userId ? e.CompletedByB : e.CompletedByA,
            IFinished = e.UserAId == userId ? e.CompletedByA : e.CompletedByB,
            PartnerExtends = e.UserAId == userId ? e.ExtensionAcceptedByB : e.ExtensionAcceptedByA,
            IExtend = e.UserAId == userId ? e.ExtensionAcceptedByA : e.ExtensionAcceptedByB,
        }).ToListAsync(ct))
        {
            if (e.PartnerFinished && !e.IFinished)
            {
                actions.Add((e.EndDate, new ActionNeededDto(ActionTypes.FinishExchange, null, e.Id, null, e.EndDate)));
            }

            if (!e.IsExtended && e.PartnerExtends && !e.IExtend)
            {
                actions.Add((e.EndDate, new ActionNeededDto(ActionTypes.AnswerExtension, null, e.Id, null, e.EndDate)));
            }
        }

        return actions.OrderBy(a => a.Due).Select(a => a.Dto).ToList();   // самое срочное сверху
    }
}
