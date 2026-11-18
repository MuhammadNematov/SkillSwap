using Microsoft.Extensions.Time.Testing;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Tests.Chat;

public class PaymentContentDetectorTests
{
    [Theory]
    [InlineData("4111111111111111")]           // тестовая Visa
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("5500 0000 0000 0004")]        // тестовая Mastercard
    [InlineData("карта: 4111 1111 1111 1111, жду")]
    [InlineData("378282246310005")]            // American Express, 15 цифр
    public void A_valid_card_number_is_found(string text) => Assert.True(PaymentContentDetector.ContainsCardNumber(text));

    [Theory]
    [InlineData("4111 1111 1111 1112")]        // контрольная сумма Луна не сходится
    [InlineData("1234 5678 9012 3456")]
    [InlineData("в 18:00, аудитория 301")]
    [InlineData("12345678")]
    [InlineData("телефон +992 900 12 34 56")]
    [InlineData("")]
    public void Other_numbers_are_not_cards(string text) => Assert.False(PaymentContentDetector.ContainsCardNumber(text));

    [Theory]
    [InlineData("переведи мне деньги")]
    [InlineData("Перевод на карту, пожалуйста")]
    [InlineData("пришли оплату вечером")]
    [InlineData("скинь деньги на карту")]
    [InlineData("отправь на мою карту")]
    [InlineData("Please pay me 20 dollars")]
    [InlineData("send me money first")]
    [InlineData("use PayPal")]
    [InlineData("my IBAN is DE89")]
    [InlineData("pay in USDT")]
    [InlineData("пул гузаронед")]
    [InlineData("пардохт кунед")]
    [InlineData("ба корт")]
    public void Payment_requests_are_found_in_three_languages(string text) => Assert.True(PaymentContentDetector.LooksLikePayment(text));

    [Theory]
    [InlineData("Привет! Урок в субботу в 18:00?")]
    [InlineData("Покажи, где это на карте мира")]       // «на карте» без «мою» не призыв к оплате
    [InlineData("Let's practise English conversation")]
    [InlineData("Салом! Дарси мо рӯзи шанбе аст")]
    [InlineData("бесплатно, обмен навыками")]
    [InlineData("")]
    [InlineData("   ")]
    public void Ordinary_chat_is_not_flagged(string text) => Assert.False(PaymentContentDetector.LooksLikePayment(text));
}

public class ChatThrottleTests
{
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public void The_twentyfirst_message_within_ten_seconds_is_refused()
    {
        var throttle = new ChatThrottle(new FakeTimeProvider());

        for (var i = 0; i < ChatThrottle.Limit; i++)
        {
            throttle.Hit(User);
        }

        var ex = Assert.Throws<TooManyRequestsException>(() => throttle.Hit(User));
        Assert.Equal("chat_rate_limited", ex.Code);
    }

    [Fact]
    public void The_window_slides_and_old_hits_expire()
    {
        var clock = new FakeTimeProvider();
        var throttle = new ChatThrottle(clock);
        for (var i = 0; i < ChatThrottle.Limit; i++)
        {
            throttle.Hit(User);
        }

        clock.Advance(ChatThrottle.Window);

        throttle.Hit(User);   // окно прошло: снова можно
    }

    [Fact]
    public void Users_are_counted_separately()
    {
        var throttle = new ChatThrottle(new FakeTimeProvider());
        for (var i = 0; i < ChatThrottle.Limit; i++)
        {
            throttle.Hit(User);
        }

        throttle.Hit(Guid.NewGuid());   // другой пользователь не пострадал
    }
}

public class ChatAccessTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_active_exchange_is_read_write() =>
        Assert.Equal(ChatMode.ReadWrite, ChatAccess.Evaluate(ExchangeStatus.Active, null, Now).Mode);

    [Fact]
    public void A_frozen_exchange_is_read_only_without_a_deadline()
    {
        var state = ChatAccess.Evaluate(ExchangeStatus.Frozen, null, Now);

        Assert.Equal(ChatMode.ReadOnly, state.Mode);
        Assert.Equal("frozen", state.ReadOnlyReason);
        Assert.Null(state.ReadableUntil);
    }

    [Theory]
    [InlineData(ExchangeStatus.Completed, "completed")]
    [InlineData(ExchangeStatus.Aborted, "aborted")]
    public void A_closed_exchange_is_read_only_for_30_days_then_archived(ExchangeStatus status, string reason)
    {
        var closed = Now.AddDays(-10);

        var inside = ChatAccess.Evaluate(status, closed, Now);
        Assert.Equal(ChatMode.ReadOnly, inside.Mode);
        Assert.Equal(reason, inside.ReadOnlyReason);
        Assert.Equal(closed.AddDays(30), inside.ReadableUntil);

        Assert.Equal(ChatMode.ReadOnly, ChatAccess.Evaluate(status, Now.AddDays(-30).AddSeconds(1), Now).Mode);
        Assert.Equal(ChatMode.Archived, ChatAccess.Evaluate(status, Now.AddDays(-30), Now).Mode);   // ровно 30 дней — уже архив
        Assert.Equal(ChatMode.Archived, ChatAccess.Evaluate(status, Now.AddDays(-90), Now).Mode);
    }
}
