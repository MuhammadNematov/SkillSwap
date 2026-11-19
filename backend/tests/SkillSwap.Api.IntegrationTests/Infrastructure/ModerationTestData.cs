using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Auth;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>Служебный аккаунт для тестов: пользователь и его настоящий access-токен (вход служебных идёт через 2FA, это проверяют отдельные тесты).</summary>
public sealed record StaffPerson(User User, string Token)
{
    public Guid Id => User.Id;
}

/// <summary>Данные для тестов отзывов и обращений: обмен двух людей, служебные аккаунты, сообщения чата.</summary>
public static class ModerationTestData
{
    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    /// <summary>Служебный аккаунт с ролью; <paramref name="linkedPersonal"/> — личный аккаунт сотрудника (конфликт интересов).</summary>
    public static async Task<StaffPerson> StaffAsync(ApiFactory factory, UserRole role, MatchPerson? linkedPersonal = null)
    {
        var user = User.CreateStaff(AuthApi.NewEmail(), role + " (test)", role);
        if (linkedPersonal is not null)
        {
            user.LinkPersonalAccount(linkedPersonal.User);
        }

        await factory.QueryAsync(async db =>
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return 0;
        });
        return new StaffPerson(user, TestTokens.ForUser(factory, user));
    }

    /// <summary>Два пользователя с анкетами и активный обмен между ними (через настоящий API: заявка и принятие).</summary>
    public static async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> ExchangeAsync(ApiFactory factory, HttpClient client)
    {
        var world = await MatchTestWorld.CreateAsync(factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        await factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id || u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });

        var created = await Send(client, a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var accepted = await Send(client, b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
        return (world, a, b, (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("exchangeId").GetGuid());
    }

    /// <summary>Сообщение в чате обмена, записанное прямо в БД (текст шифруется настоящим шифром приложения).</summary>
    public static async Task<Guid> MessageAsync(ApiFactory factory, Guid exchangeId, Guid senderId, string text)
    {
        using var scope = factory.Services.CreateScope();
        var cipher = scope.ServiceProvider.GetRequiredService<IMessageCipher>();
        var message = Message.CreateText(exchangeId, senderId, cipher.Encrypt(text), paymentWarning: false);
        await factory.QueryAsync(async db =>
        {
            db.Messages.Add(message);
            await db.SaveChangesAsync();
            return 0;
        });
        return message.Id;
    }

    /// <summary>Субъект запроса, как его собирает проверка access-токена: клеймы <c>sub</c> и <c>role</c>.</summary>
    public static ClaimsPrincipal PrincipalFor(User user) => new(new ClaimsIdentity(
        [new Claim("sub", user.Id.ToString()), new Claim(JwtAccessTokenGenerator.RoleClaim, user.Role.ToString())],
        authenticationType: "Bearer", nameType: "sub", roleType: JwtAccessTokenGenerator.RoleClaim));

    public static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static async Task<HttpResponseMessage> Send(HttpClient client, MatchPerson who, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }
}
