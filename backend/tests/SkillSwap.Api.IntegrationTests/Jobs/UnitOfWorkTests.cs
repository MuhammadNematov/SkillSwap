using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Api.IntegrationTests.Jobs;

/// <summary>Транзакция и блокировка пользователей по возрастанию Id (раздел 10.2 ТЗ) на настоящем PostgreSQL.</summary>
[Collection(ApiCollection.Name)]
public class UnitOfWorkTests
{
    private readonly ApiFactory _factory;

    public UnitOfWorkTests(ApiFactory factory) => _factory = factory;

    private async Task<Guid[]> NewUsersAsync(int count)
    {
        var users = Enumerable.Range(0, count).Select(_ => User.CreateRegular(AuthApi.NewEmail(), "Lock", new DateOnly(1990, 1, 1), "en")).ToList();
        await _factory.QueryAsync(async db =>
        {
            db.Users.AddRange(users);
            await db.SaveChangesAsync();
            return 0;
        });
        return users.Select(u => u.Id).ToArray();
    }

    private IServiceScope Scope() => _factory.Services.CreateScope();

    [Fact]
    public async Task Locking_outside_a_transaction_is_refused_because_it_would_release_at_once()
    {
        using var scope = Scope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => uow.LockUsersAsync([Guid.NewGuid()], CancellationToken.None));
    }

    [Fact]
    public async Task A_locked_user_makes_another_transaction_wait_until_the_first_one_finishes()
    {
        var ids = await NewUsersAsync(1);
        var firstHolds = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = Task.Run(async () =>
        {
            using var scope = Scope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.ExecuteAsync(async ct =>
            {
                await uow.LockUsersAsync(ids, ct);
                firstHolds.SetResult();
                await releaseFirst.Task;
                return 0;
            }, CancellationToken.None);
        });
        await firstHolds.Task;

        var secondGotIt = false;
        var second = Task.Run(async () =>
        {
            using var scope = Scope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.ExecuteAsync(async ct =>
            {
                await uow.LockUsersAsync(ids, ct);
                secondGotIt = true;
                return 0;
            }, CancellationToken.None);
        });

        await Task.Delay(700);
        Assert.False(secondGotIt);              // ждёт, пока первый держит строку
        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(secondGotIt);               // и проходит сразу после
    }

    [Fact]
    public async Task The_same_pair_in_opposite_orders_never_deadlocks()
    {
        var ids = await NewUsersAsync(2);
        var (a, b) = (ids[0], ids[1]);

        // Сорок транзакций берут одну и ту же пару то в одном, то в другом порядке и держат блокировку.
        // Без единого порядка захвата это даёт взаимную блокировку PostgreSQL (ошибка 40P01).
        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            using var scope = Scope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.ExecuteAsync(async ct =>
            {
                await uow.LockUsersAsync(i % 2 == 0 ? [a, b] : [b, a], ct);
                await Task.Delay(15, ct);
                return 0;
            }, CancellationToken.None);
        }));

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));   // исключение или зависание = провал
    }

    [Fact]
    public async Task Three_users_in_any_order_with_repeats_are_locked_without_trouble()
    {
        var ids = await NewUsersAsync(3);
        var tasks = Enumerable.Range(0, 24).Select(i => Task.Run(async () =>
        {
            using var scope = Scope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var order = ids.OrderBy(_ => Random.Shared.Next()).Concat([ids[i % 3]]).ToArray();   // с повтором
            await uow.ExecuteAsync(async ct =>
            {
                await uow.LockUsersAsync(order, ct);
                await Task.Delay(10, ct);
                return 0;
            }, CancellationToken.None);
        }));

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Unknown_ids_are_skipped_without_an_error()
    {
        using var scope = Scope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var result = await uow.ExecuteAsync(async ct =>
        {
            await uow.LockUsersAsync([Guid.NewGuid(), Guid.NewGuid()], ct);
            return 42;
        }, CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task An_exception_rolls_everything_back_and_success_commits()
    {
        var marker = "UoW" + Guid.NewGuid().ToString("N")[..8];

        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SkillSwap.Infrastructure.Persistence.AppDbContext>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteAsync<int>(async ct =>
            {
                db.SkillCategories.Add(SkillCategory.Create(marker, marker, marker + "-fail", 1));
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException("откат");
            }, CancellationToken.None));
        }

        Assert.False(await _factory.QueryAsync(db => db.SkillCategories.AnyAsync(c => c.NameEn == marker + "-fail")));

        using (var scope = Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SkillSwap.Infrastructure.Persistence.AppDbContext>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.ExecuteAsync(async ct =>
            {
                var created = SkillCategory.Create(marker, marker, marker + "-ok", 9000);
                created.Update(marker, marker, marker + "-ok", 9000, isActive: false);   // выключена: другие тесты каталога её не видят
                db.SkillCategories.Add(created);
                await db.SaveChangesAsync(ct);
                return 0;
            }, CancellationToken.None);
        }

        Assert.True(await _factory.QueryAsync(db => db.SkillCategories.AnyAsync(c => c.NameEn == marker + "-ok")));
    }

    [Fact]
    public async Task A_nested_call_joins_the_outer_transaction()
    {
        var marker = "Nested" + Guid.NewGuid().ToString("N")[..8];
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<SkillSwap.Infrastructure.Persistence.AppDbContext>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteAsync<int>(async outer =>
        {
            await uow.ExecuteAsync(async ct =>
            {
                db.SkillCategories.Add(SkillCategory.Create(marker, marker, marker, 1));
                await db.SaveChangesAsync(ct);
                return 0;   // внутренний «успех» ещё не фиксирует: транзакцией владеет внешний
            }, outer);
            throw new InvalidOperationException("внешний откат");
        }, CancellationToken.None));

        Assert.False(await _factory.QueryAsync(c => c.SkillCategories.AnyAsync(x => x.NameEn == marker)));
    }
}
