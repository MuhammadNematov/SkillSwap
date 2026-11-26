using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Telegram;

/// <summary>Подключение Telegram к аккаунту: статус, одноразовый код, команды бота <c>/start</c> и <c>/stop</c>, отключение (в том числе когда бот заблокирован).</summary>
public sealed class TelegramService
{
    private readonly ITelegramStore _store;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly ITelegramStatusPusher _status;
    private readonly TelegramOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(
        ITelegramStore store, IProfileStore profiles, IUnitOfWork uow, IAuditLog audit, ITelegramStatusPusher status,
        IOptions<TelegramOptions> options, TimeProvider clock, ILogger<TelegramService> logger)
    {
        _store = store;
        _profiles = profiles;
        _uow = uow;
        _audit = audit;
        _status = status;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    public async Task<TelegramStatusDto> StatusAsync(Guid userId, CancellationToken ct)
    {
        await RequireRegularAsync(userId, ct);
        if (!_options.IsConfigured)
        {
            return new TelegramStatusDto(false, false, null, null);
        }

        var link = await _store.FindByUserAsync(userId, ct);
        return new TelegramStatusDto(true, link is not null, _options.BotName, link?.LinkedAt);
    }

    /// <summary>Новый одноразовый код (прежние перестают действовать). В базе только его хеш.</summary>
    public async Task<TelegramLinkDto> CreateLinkAsync(Guid userId, CancellationToken ct)
    {
        await RequireRegularAsync(userId, ct);
        if (!_options.IsConfigured)
        {
            throw new ServiceUnavailableException("telegram_not_configured", "Уведомления в Telegram не настроены.");
        }

        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));   // 32 символа из разрешённых Telegram для start
        var entity = await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([userId], token);
            await _store.DeleteCodesAsync(userId, token);
            var created = TelegramLinkCode.Create(userId, Hash(code), Now());
            _store.Add(created);
            await _store.SaveAsync(token);
            return created;
        }, ct);

        return new TelegramLinkDto($"https://t.me/{Uri.EscapeDataString(_options.BotName)}?start={code}", entity.ExpiresAt);
    }

    public async Task<TelegramStatusDto> UnlinkAsync(Guid userId, CancellationToken ct)
    {
        await RequireRegularAsync(userId, ct);
        var link = await _store.FindByUserAsync(userId, ct);
        if (link is not null)
        {
            await RemoveAsync(link, ct);
        }

        return await StatusAsync(userId, ct);
    }

    /// <summary>Бот получил <c>/start код</c>. Возвращает текст ответа на языке пользователя (или собеседника, если код не найден).</summary>
    public async Task<string> StartAsync(long chatId, string? code, string? languageCode, CancellationToken ct)
    {
        var fallback = LanguageOf(languageCode);
        if (string.IsNullOrWhiteSpace(code))
        {
            return TelegramTexts.StartHint(await ChatLanguageAsync(chatId, fallback, ct));
        }

        var hash = Hash(code.Trim());
        var peek = await _store.FindCodeAsync(hash, ct);
        if (peek is null)
        {
            return TelegramTexts.InvalidCode(fallback);
        }

        var user = await _profiles.FindUserAsync(peek.UserId, ct);
        if (user is null || !user.IsRegular || user.Status != UserStatus.Active)
        {
            return TelegramTexts.InvalidCode(fallback);
        }

        var previousOwner = await _store.FindByChatAsync(chatId, ct);
        var ids = new List<Guid> { user.Id };
        if (previousOwner is not null)
        {
            ids.Add(previousOwner.UserId);
        }

        Guid? movedFrom = null;
        var linked = await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync(ids, token);
            var now = Now();
            var entry = await _store.FindCodeAsync(hash, token);
            var current = await _store.FindByChatAsync(chatId, token);
            if (entry is null || !entry.IsUsable(now))
            {
                // Повторная доставка того же /start (webhook повторяет запрос): этот чат уже подключён этим кодом
                return entry is not null && entry.UsedByChatId == chatId && current?.UserId == entry.UserId;
            }

            var own = await _store.FindByUserAsync(entry.UserId, token);
            if (current is not null && current.UserId != entry.UserId)
            {
                movedFrom = current.UserId;
                _store.Remove(current);
            }

            if (own is not null && own.ChatId != chatId)
            {
                _store.Remove(own);
            }

            // Сначала снимаем старые связи (уникальные индексы по пользователю и чату), потом создаём новую
            await _store.SaveAsync(token);
            if (own is null || own.ChatId != chatId)
            {
                _store.Add(TelegramLink.Create(entry.UserId, chatId, now));
            }

            entry.Use(chatId, now);
            if (movedFrom is { } from)
            {
                _audit.Add(new AuditEntry(entry.UserId, "TelegramLinkMoved", "User", from, new { fromUserId = from, toUserId = entry.UserId }));
            }

            await _store.SaveAsync(token);
            return true;
        }, ct);

        if (!linked)
        {
            return TelegramTexts.InvalidCode(user.UiLanguage);
        }

        await PushAsync(user.Id, true, ct);
        if (movedFrom is { } old)
        {
            await PushAsync(old, false, ct);
        }

        return TelegramTexts.Linked(user.UiLanguage);
    }

    /// <summary>Команда <c>/stop</c>: отвязать этот чат.</summary>
    public async Task<string> StopAsync(long chatId, string? languageCode, CancellationToken ct)
    {
        var link = await _store.FindByChatAsync(chatId, ct);
        if (link is null)
        {
            return TelegramTexts.NotLinked(LanguageOf(languageCode));
        }

        var language = (await _profiles.FindUserAsync(link.UserId, ct))?.UiLanguage ?? LanguageOf(languageCode);
        await RemoveAsync(link, ct);
        return TelegramTexts.Unlinked(language);
    }

    /// <summary>Пользователь заблокировал бота (403): связь снимается сама.</summary>
    public async Task UnlinkBlockedAsync(long chatId, CancellationToken ct)
    {
        var link = await _store.FindByChatAsync(chatId, ct);
        if (link is null)
        {
            return;
        }

        _logger.LogInformation("Telegram: бот заблокирован пользователем {UserId}, связь снята.", link.UserId);
        await RemoveAsync(link, ct);
    }

    private async Task RemoveAsync(TelegramLink link, CancellationToken ct)
    {
        _store.Remove(link);
        await _store.SaveAsync(ct);
        await PushAsync(link.UserId, false, ct);
    }

    private async Task PushAsync(Guid userId, bool connected, CancellationToken ct)
    {
        try
        {
            await _status.PushAsync(userId, connected, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось сообщить вкладкам о связи с Telegram");   // страница обновит статус опросом
        }
    }

    private async Task<string> ChatLanguageAsync(long chatId, string fallback, CancellationToken ct)
    {
        var link = await _store.FindByChatAsync(chatId, ct);
        return link is null ? fallback : (await _profiles.FindUserAsync(link.UserId, ct))?.UiLanguage ?? fallback;
    }

    private static string LanguageOf(string? languageCode) => languageCode?.ToLowerInvariant() switch
    {
        { } l when l.StartsWith("ru", StringComparison.Ordinal) => "ru",
        { } l when l.StartsWith("tg", StringComparison.Ordinal) => "tg",
        _ => "en",
    };

    internal static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private async Task RequireRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Уведомления в Telegram доступны только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }
}

/// <summary>Разбор входящих сообщений бота (webhook и long polling): только личные чаты и команды <c>/start</c>, <c>/stop</c>.</summary>
public sealed class TelegramUpdateHandler
{
    private readonly TelegramService _service;
    private readonly ITelegramClient _client;

    public TelegramUpdateHandler(TelegramService service, ITelegramClient client)
    {
        _service = service;
        _client = client;
    }

    public async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        // Группы и каналы не подключаются: личные уведомления не должны попадать в общий чат
        if (update.Message is not { IsPrivate: true, Text: { } text } message)
        {
            return;
        }

        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts.Length > 0 ? parts[0].Split('@')[0].ToLowerInvariant() : string.Empty;
        var reply = command switch
        {
            "/start" => await _service.StartAsync(message.ChatId, parts.Length > 1 ? parts[1] : null, message.LanguageCode, ct),
            "/stop" => await _service.StopAsync(message.ChatId, message.LanguageCode, ct),
            _ => null,
        };

        if (reply is not null)
        {
            var result = await _client.SendMessageAsync(message.ChatId, reply, null, ct);
            if (result.Status == TelegramSendStatus.Blocked)
            {
                await _service.UnlinkBlockedAsync(message.ChatId, ct);
            }
        }
    }
}
