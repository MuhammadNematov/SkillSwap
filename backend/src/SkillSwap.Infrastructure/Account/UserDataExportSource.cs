using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Account;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Account;

/// <summary>
/// Выгрузка данных пользователя в JSON (раздел 8.1 ТЗ): профиль, языки и навыки, заявки, обмены, уроки, <b>свои</b> сообщения (расшифрованные),
/// блокировки и отзывы (написанные и полученные). Чужие сообщения и чужие личные данные в выгрузку не попадают: у партнёров только имя,
/// как оно показано пользователю. Пароль, ключи 2FA и токены не выгружаются.
/// </summary>
public sealed class UserDataExportSource : IUserDataExportSource
{
    public const int SchemaVersion = 1;

    private sealed record SkillNames(string Tg, string Ru, string En);

    private readonly AppDbContext _db;
    private readonly IMessageCipher _cipher;

    public UserDataExportSource(AppDbContext db, IMessageCipher cipher)
    {
        _db = db;
        _cipher = cipher;
    }

    public async Task WriteAsync(Guid userId, Stream output, DateTime now, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        var skills = await _db.Skills.AsNoTracking().ToDictionaryAsync(s => s.Id, s => new SkillNames(s.NameTg, s.NameRu, s.NameEn), ct);
        var exchanges = await _db.Exchanges.AsNoTracking().Where(e => e.UserAId == userId || e.UserBId == userId).OrderBy(e => e.CreatedAt).ToListAsync(ct);
        var requests = await _db.ExchangeRequests.AsNoTracking().Where(r => r.FromUserId == userId || r.ToUserId == userId).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var blocks = await _db.UserBlocks.AsNoTracking().Where(b => b.UserId == userId).OrderBy(b => b.CreatedAt).ToListAsync(ct);

        var otherIds = requests.Select(r => r.FromUserId == userId ? r.ToUserId : r.FromUserId)
            .Concat(exchanges.Select(e => e.UserAId == userId ? e.UserBId : e.UserAId))
            .Concat(blocks.Select(b => b.BlockedUserId)).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => otherIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Status == UserStatus.Deleted ? User.DeletedDisplayName : u.Name, ct);

        await using var json = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // кириллица читается как есть
        });
        json.WriteStartObject();
        json.WriteNumber("schemaVersion", SchemaVersion);
        json.WriteString("generatedAt", now);

        WriteAccount(json, user);
        await WriteLanguagesAndSkillsAsync(json, userId, ct);
        WriteRequests(json, userId, requests, names, skills);
        WriteExchanges(json, userId, exchanges, names, skills);
        await WriteSessionsAsync(json, exchanges.Select(e => e.Id).ToList(), ct);
        await WriteMessagesAsync(json, userId, ct);

        json.WriteStartArray("blockedUsers");
        foreach (var b in blocks)
        {
            json.WriteStartObject();
            json.WriteString("name", names.GetValueOrDefault(b.BlockedUserId));
            json.WriteString("blockedAt", b.CreatedAt);
            json.WriteEndObject();
        }

        json.WriteEndArray();

        await WriteReviewsAsync(json, userId, names, ct);
        await WriteNotificationSettingsAsync(json, userId, ct);

        json.WriteEndObject();
        await json.FlushAsync(ct);
    }

    /// <summary>Связь с Telegram (идентификатор чата — данные пользователя) и выключенные или включённые вручную переключатели уведомлений.</summary>
    private async Task WriteNotificationSettingsAsync(Utf8JsonWriter json, Guid userId, CancellationToken ct)
    {
        var link = await _db.TelegramLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId, ct);
        if (link is null)
        {
            json.WriteNull("telegram");
        }
        else
        {
            json.WriteStartObject("telegram");
            json.WriteNumber("chatId", link.ChatId);
            json.WriteString("linkedAt", link.LinkedAt);
            json.WriteEndObject();
        }

        json.WriteStartArray("notificationSettings");
        foreach (var p in await _db.NotificationPreferences.AsNoTracking().Where(p => p.UserId == userId).OrderBy(p => p.Category).ThenBy(p => p.Channel).ToListAsync(ct))
        {
            json.WriteStartObject();
            json.WriteString("category", p.Category.ToString());
            json.WriteString("channel", p.Channel.ToString());
            json.WriteBoolean("enabled", p.Enabled);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// Отзывы (раздел 8.1 ТЗ). Написанные пользователем выгружаются все, в том числе ещё не опубликованные и скрытые (это его собственный текст). Полученные
    /// только опубликованные: отзыв обо мне, который ещё скрыт слепой публикацией, я увидеть не должен и в выгрузке тоже.
    /// </summary>
    private async Task WriteReviewsAsync(Utf8JsonWriter json, Guid userId, IReadOnlyDictionary<Guid, string> names, CancellationToken ct)
    {
        var written = await _db.Reviews.AsNoTracking().Where(r => r.AuthorId == userId).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var received = await _db.Reviews.AsNoTracking()
            .Where(r => r.TargetId == userId && (r.Status == ReviewStatus.Published || r.Status == ReviewStatus.Frozen))
            .OrderBy(r => r.PublishedAt).ToListAsync(ct);

        var peopleIds = written.Select(r => r.TargetId).Concat(received.Select(r => r.AuthorId)).Where(id => !names.ContainsKey(id)).Distinct().ToList();
        var more = await _db.Users.AsNoTracking().Where(u => peopleIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Status == UserStatus.Deleted ? User.DeletedDisplayName : u.Name, ct);
        string NameOf(Guid id) => names.TryGetValue(id, out var n) ? n : more.GetValueOrDefault(id, User.DeletedDisplayName);

        void Write(string name, IEnumerable<Review> reviews, Func<Review, Guid> otherSide, string otherKey, bool withStatus)
        {
            json.WriteStartArray(name);
            foreach (var r in reviews)
            {
                json.WriteStartObject();
                json.WriteString("id", r.Id);
                json.WriteString("exchangeId", r.ExchangeId);
                json.WriteString(otherKey, NameOf(otherSide(r)));
                json.WriteNumber("scoreKnowledge", r.ScoreKnowledge);
                json.WriteNumber("scoreExplaining", r.ScoreExplaining);
                json.WriteNumber("scorePunctuality", r.ScorePunctuality);
                json.WriteNumber("scorePoliteness", r.ScorePoliteness);
                json.WriteString("text", r.Text);
                json.WriteBoolean("isFullExchange", r.IsFullExchange);
                if (withStatus)
                {
                    json.WriteString("status", r.Status.ToString());
                }

                json.WriteString("createdAt", r.CreatedAt);
                if (r.PublishedAt is { } publishedAt)
                {
                    json.WriteString("publishedAt", publishedAt);
                }
                else
                {
                    json.WriteNull("publishedAt");
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        Write("reviewsWritten", written, r => r.TargetId, "about", withStatus: true);
        Write("reviewsReceived", received, r => r.AuthorId, "from", withStatus: false);
    }

    private static void WriteAccount(Utf8JsonWriter json, User user)
    {
        json.WriteStartObject("account");
        json.WriteString("id", user.Id);
        json.WriteString("email", user.Email);
        json.WriteString("name", user.Name);
        json.WriteString("bio", user.Bio);
        json.WriteString("dateOfBirth", user.DateOfBirth?.ToString("yyyy-MM-dd"));
        json.WriteString("uiLanguage", user.UiLanguage);
        json.WriteString("timeZone", user.TimeZone);
        json.WriteBoolean("isSearchable", user.IsSearchable);
        json.WriteBoolean("twoFactorEnabled", user.TwoFactorEnabled);
        json.WriteBoolean("hasAvatar", user.AvatarKey is not null);
        json.WriteString("createdAt", user.CreatedAt);
        json.WriteNullable("lastActiveAt", user.LastActiveAt);
        json.WriteEndObject();
    }

    private async Task WriteLanguagesAndSkillsAsync(Utf8JsonWriter json, Guid userId, CancellationToken ct)
    {
        json.WriteStartArray("lessonLanguages");
        foreach (var code in await _db.UserLanguages.AsNoTracking().Where(l => l.UserId == userId).Select(l => l.LanguageCode).OrderBy(c => c).ToListAsync(ct))
        {
            json.WriteStringValue(code);
        }

        json.WriteEndArray();

        var rows = await (from us in _db.UserSkills.AsNoTracking()
                          join s in _db.Skills.AsNoTracking() on us.SkillId equals s.Id
                          where us.UserId == userId
                          orderby s.NameEn
                          select new { us.Direction, us.Level, us.Description, s.NameTg, s.NameRu, s.NameEn }).ToListAsync(ct);
        foreach (var direction in new[] { SkillDirection.Teach, SkillDirection.Learn })
        {
            json.WriteStartArray(direction == SkillDirection.Teach ? "skillsTeach" : "skillsLearn");
            foreach (var s in rows.Where(x => x.Direction == direction))
            {
                json.WriteStartObject();
                WriteSkill(json, "skill", new SkillNames(s.NameTg, s.NameRu, s.NameEn));
                json.WriteString("level", s.Level.ToString());
                json.WriteString("description", s.Description);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }
    }

    private static void WriteSkill(Utf8JsonWriter json, string property, SkillNames? names)
    {
        if (names is null)
        {
            return;
        }

        json.WriteStartObject(property);
        json.WriteString("tg", names.Tg);
        json.WriteString("ru", names.Ru);
        json.WriteString("en", names.En);
        json.WriteEndObject();
    }

    private static void WriteRequests(
        Utf8JsonWriter json, Guid userId, List<ExchangeRequest> requests, Dictionary<Guid, string> names, Dictionary<Guid, SkillNames> skills)
    {
        json.WriteStartArray("exchangeRequests");
        foreach (var r in requests)
        {
            var outgoing = r.FromUserId == userId;
            json.WriteStartObject();
            json.WriteString("id", r.Id);
            json.WriteString("direction", outgoing ? "outgoing" : "incoming");
            json.WriteString("otherUser", names.GetValueOrDefault(outgoing ? r.ToUserId : r.FromUserId));
            json.WriteString("status", r.Status.ToString());
            WriteSkill(json, "senderTeaches", r.FromTeachesSkillId is { } from ? skills.GetValueOrDefault(from) : null);
            WriteSkill(json, "recipientTeaches", skills.GetValueOrDefault(r.ToTeachesSkillId));
            json.WriteNumber("durationDays", r.DurationDays);
            json.WriteString("message", r.Message);
            json.WriteString("createdAt", r.CreatedAt);
            json.WriteString("expiresAt", r.ExpiresAt);
            json.WriteNullable("respondedAt", r.RespondedAt);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteExchanges(
        Utf8JsonWriter json, Guid userId, List<Exchange> exchanges, Dictionary<Guid, string> names, Dictionary<Guid, SkillNames> skills)
    {
        json.WriteStartArray("exchanges");
        foreach (var e in exchanges)
        {
            var isA = e.UserAId == userId;
            json.WriteStartObject();
            json.WriteString("id", e.Id);
            json.WriteString("status", e.Status.ToString());
            json.WriteString("partner", names.GetValueOrDefault(isA ? e.UserBId : e.UserAId));
            WriteSkill(json, "iTeach", skills.GetValueOrDefault(isA ? e.SkillAId : e.SkillBId));
            WriteSkill(json, "iLearn", skills.GetValueOrDefault(isA ? e.SkillBId : e.SkillAId));
            json.WriteString("startDate", e.StartDate);
            json.WriteString("endDate", e.EndDate);
            json.WriteBoolean("isExtended", e.IsExtended);
            json.WriteNullable("closedAt", e.ClosedAt);
            json.WriteBoolean("abortedByMe", e.AbortedById == userId);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private async Task WriteSessionsAsync(Utf8JsonWriter json, List<Guid> exchangeIds, CancellationToken ct)
    {
        json.WriteStartArray("sessions");
        foreach (var s in await _db.Sessions.AsNoTracking().Where(s => exchangeIds.Contains(s.ExchangeId)).OrderBy(s => s.StartAt).ToListAsync(ct))
        {
            json.WriteStartObject();
            json.WriteString("id", s.Id);
            json.WriteString("exchangeId", s.ExchangeId);
            json.WriteString("startAt", s.StartAt);
            json.WriteString("endAt", s.EndAt);
            json.WriteString("topic", s.Topic);
            json.WriteString("status", s.Status.ToString());
            json.WriteString("teacherId", s.TeacherId);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>Только сообщения самого пользователя; текст расшифрован, удалённые помечены (они тоже его данные).</summary>
    private async Task WriteMessagesAsync(Utf8JsonWriter json, Guid userId, CancellationToken ct)
    {
        json.WriteStartArray("messages");
        var attachments = await _db.Attachments.AsNoTracking().Where(a => a.OwnerId == userId).ToDictionaryAsync(a => a.Id, ct);
        await foreach (var m in _db.Messages.AsNoTracking().Where(m => m.SenderId == userId).OrderBy(m => m.CreatedAt).AsAsyncEnumerable().WithCancellation(ct))
        {
            json.WriteStartObject();
            json.WriteString("id", m.Id);
            json.WriteString("exchangeId", m.ExchangeId);
            json.WriteString("type", m.Type.ToString());
            json.WriteString("text", m.EncryptedContent is null ? null : _cipher.Decrypt(m.EncryptedContent));
            if (m.AttachmentId is { } a && attachments.TryGetValue(a, out var attachment))
            {
                json.WriteStartObject("attachment");
                json.WriteString("fileName", attachment.FileName);
                json.WriteString("mimeType", attachment.MimeType);
                json.WriteNumber("sizeBytes", attachment.SizeBytes);
                json.WriteEndObject();
            }

            json.WriteNullable("sessionId", m.SessionId);
            json.WriteString("createdAt", m.CreatedAt);
            json.WriteNullable("editedAt", m.EditedAt);
            json.WriteNullable("deletedAt", m.DeletedAt);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }
}

internal static class JsonWriterExtensions
{
    public static void WriteNullable(this Utf8JsonWriter json, string name, DateTime? value)
    {
        if (value is { } v)
        {
            json.WriteString(name, v);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    public static void WriteNullable(this Utf8JsonWriter json, string name, Guid? value)
    {
        if (value is { } v)
        {
            json.WriteString(name, v);
        }
        else
        {
            json.WriteNull(name);
        }
    }
}
