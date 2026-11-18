using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SkillSwap.Api.RateLimiting;

namespace SkillSwap.Api.IntegrationTests.RateLimiting;

/// <summary>Составные части лимитера без запуска приложения и без БД.</summary>
public class RateLimitingUnitTests
{
    // ---------- ключ клиента ----------

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]   // IPv4 в обёртке IPv6 — тот же клиент
    public void An_IPv4_client_is_keyed_by_its_address(string address, string expected)
    {
        Assert.Equal(expected, ClientIp.KeyFor(IPAddress.Parse(address)));
    }

    [Fact]
    public void An_IPv6_client_is_keyed_by_its_64_bit_prefix_so_one_subnet_is_one_client()
    {
        var a = ClientIp.KeyFor(IPAddress.Parse("2001:db8:aaaa:bbbb::1"));
        var sameSubnet = ClientIp.KeyFor(IPAddress.Parse("2001:db8:aaaa:bbbb:ffff:ffff:ffff:ffff"));
        var otherSubnet = ClientIp.KeyFor(IPAddress.Parse("2001:db8:aaaa:cccc::1"));

        Assert.Equal(a, sameSubnet);      // смена адреса внутри /64 лимит не обходит
        Assert.NotEqual(a, otherSubnet);  // а другая подсеть — другой клиент
        Assert.EndsWith("/64", a);
    }

    [Fact]
    public void A_client_without_an_address_gets_a_shared_unknown_key()
    {
        Assert.Equal(ClientIp.Unknown, ClientIp.KeyFor((IPAddress?)null));
    }

    // ---------- лимит по email ----------

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    private EmailRateLimiter Limiter(int permits, int windowSeconds) =>
        new(Options.Create(new RateLimitingOptions { MailPerEmail = new RateLimitRule(permits, windowSeconds) }), _clock);

    [Fact]
    public void The_limiter_lets_the_permitted_number_through_and_then_refuses_with_the_exact_retry_time()
    {
        using var limiter = Limiter(3, 3600);

        Assert.True(limiter.TryAcquire("a@example.com", out _));
        Assert.True(limiter.TryAcquire("a@example.com", out _));
        Assert.True(limiter.TryAcquire("a@example.com", out _));

        Assert.False(limiter.TryAcquire("a@example.com", out var retryAfter));
        Assert.Equal(TimeSpan.FromHours(1), retryAfter);   // первая попытка освободится ровно через час

        _clock.Advance(TimeSpan.FromMinutes(25));
        Assert.False(limiter.TryAcquire("a@example.com", out retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(35), retryAfter);   // а к повтору через 25 минут осталось 35
    }

    [Fact]
    public void Addresses_are_counted_independently()
    {
        using var limiter = Limiter(1, 3600);
        Assert.True(limiter.TryAcquire("a@example.com", out _));
        Assert.False(limiter.TryAcquire("a@example.com", out _));

        Assert.True(limiter.TryAcquire("b@example.com", out _));
    }

    [Theory]
    [InlineData("A@Example.com")]
    [InlineData("  a@example.com  ")]
    [InlineData("A@EXAMPLE.COM")]
    public void Case_and_surrounding_spaces_do_not_give_a_way_around_the_limit(string variant)
    {
        using var limiter = Limiter(1, 3600);
        Assert.True(limiter.TryAcquire("a@example.com", out _));

        Assert.False(limiter.TryAcquire(variant, out _));
    }

    [Fact]
    public void The_limit_recovers_after_the_window_passes()
    {
        using var limiter = Limiter(3, 3600);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire("a@example.com", out _));
        }

        Assert.False(limiter.TryAcquire("a@example.com", out var retryAfter));
        _clock.Advance(retryAfter);

        Assert.True(limiter.TryAcquire("a@example.com", out _));
    }

    // ---------- настройки ----------

    [Fact]
    public void Defaults_are_the_limits_from_the_specification()
    {
        var o = new RateLimitingOptions();

        Assert.True(o.Enabled);
        Assert.Equal((3, 3600), (o.MailPerEmail.PermitLimit, o.MailPerEmail.WindowSeconds));
        Assert.Equal((10, 3600), (o.MailPerIp.PermitLimit, o.MailPerIp.WindowSeconds));
        Assert.Equal((10, 60), (o.LoginPerIp.PermitLimit, o.LoginPerIp.WindowSeconds));
        Assert.Equal((5, 3600), (o.RegisterPerIp.PermitLimit, o.RegisterPerIp.WindowSeconds));
        Assert.Equal((10, 3600), (o.ResetPasswordPerIp.PermitLimit, o.ResetPasswordPerIp.WindowSeconds));
        Assert.Equal((20, 3600), (o.ConfirmEmailPerIp.PermitLimit, o.ConfirmEmailPerIp.WindowSeconds));
        Assert.Equal((30, 60), (o.RefreshPerIp.PermitLimit, o.RefreshPerIp.WindowSeconds));
        Assert.Equal((5, 3600), (o.SuggestionsPerUser.PermitLimit, o.SuggestionsPerUser.WindowSeconds));
        Assert.Equal((100, 60), (o.AuthenticatedPerUser.PermitLimit, o.AuthenticatedPerUser.WindowSeconds));
    }

    [Fact]
    public void Values_are_read_from_configuration_including_environment_style_keys()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = "false",
            ["RateLimiting:LoginPerIp:PermitLimit"] = "42",
            ["RateLimiting:MailPerEmail:WindowSeconds"] = "7200",
        }).Build();
        var services = new ServiceCollection();
        services.AddOptions<RateLimitingOptions>().Bind(config.GetSection(RateLimitingOptions.SectionName));

        var o = services.BuildServiceProvider().GetRequiredService<IOptions<RateLimitingOptions>>().Value;

        Assert.False(o.Enabled);
        Assert.Equal(42, o.LoginPerIp.PermitLimit);
        Assert.Equal(60, o.LoginPerIp.WindowSeconds);          // не заданное остаётся по умолчанию
        Assert.Equal(7200, o.MailPerEmail.WindowSeconds);
        Assert.Equal(3, o.MailPerEmail.PermitLimit);
    }

    [Theory]
    [InlineData("MailPerEmail", 0, 3600)]
    [InlineData("MailPerIp", -1, 3600)]
    [InlineData("LoginPerIp", 10, 0)]
    [InlineData("RegisterPerIp", 5, -5)]
    [InlineData("AuthenticatedPerUser", 0, 0)]
    [InlineData("ResetPasswordPerIp", 0, 3600)]
    [InlineData("ConfirmEmailPerIp", 20, 0)]
    [InlineData("RefreshPerIp", -3, 60)]
    [InlineData("SuggestionsPerUser", 0, 3600)]
    public void Nonsense_values_are_rejected_instead_of_silently_disabling_the_limit(string rule, int permits, int window)
    {
        var services = new ServiceCollection();
        services.AddOptions<RateLimitingOptions>()
            .Configure(o =>
            {
                var target = (RateLimitRule)typeof(RateLimitingOptions).GetProperty(rule)!.GetValue(o)!;
                target.PermitLimit = permits;
                target.WindowSeconds = window;
            })
            .ValidateDataAnnotations();
        var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RateLimitingOptions>>().Value);
    }

    [Fact]
    public void Valid_defaults_pass_validation()
    {
        var services = new ServiceCollection();
        services.AddOptions<RateLimitingOptions>().ValidateDataAnnotations();

        var value = services.BuildServiceProvider().GetRequiredService<IOptions<RateLimitingOptions>>().Value;

        Assert.NotNull(value);
        Assert.Empty(Validator.GetValidationErrors(value.LoginPerIp));
    }
}

file static class Validator
{
    public static IReadOnlyList<ValidationResult> GetValidationErrors(object instance)
    {
        var results = new List<ValidationResult>();
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }
}
