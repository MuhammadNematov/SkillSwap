using SkillSwap.Application.Settings;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Tests;

/// <summary>Реестр системных настроек: ключи, значения по умолчанию, проверка и приведение значений.</summary>
public class SystemSettingDefinitionsTests
{
    [Fact]
    public void Every_known_key_is_registered_once_and_its_default_is_valid()
    {
        var keys = typeof(SystemSettingKeys).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();

        Assert.Equal(keys.Order(), SystemSettingDefinitions.All.Select(d => d.Key).Order());
        Assert.Equal(keys.Count, SystemSettingDefinitions.All.Select(d => d.Key).Distinct().Count());
        Assert.All(SystemSettingDefinitions.All, d =>
        {
            Assert.Equal(d.Default, d.Normalize(d.Default));
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
            Assert.True(d.Description.Length <= 500);
        });
    }

    [Fact]
    public void The_defaults_match_the_limits_of_the_terms_of_reference()
    {
        Assert.Equal("true", SystemSettingDefinitions.Find(SystemSettingKeys.MinorsMatchOnlyMinors)!.Default);
        Assert.Equal("3", SystemSettingDefinitions.Find(SystemSettingKeys.MaxActiveExchanges)!.Default);
        Assert.Equal("5", SystemSettingDefinitions.Find(SystemSettingKeys.MaxPendingOutgoing)!.Default);
        Assert.Equal("10", SystemSettingDefinitions.Find(SystemSettingKeys.MaxRequestsPerDay)!.Default);
        Assert.Equal("30", SystemSettingDefinitions.Find(SystemSettingKeys.DeclineCooldownDays)!.Default);
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData(" FALSE ", "false")]
    [InlineData("True", "true")]
    [InlineData("yes", null)]
    [InlineData("1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_flag_accepts_only_true_or_false(string? raw, string? expected)
    {
        Assert.Equal(expected, SystemSettingDefinitions.Find(SystemSettingKeys.MinorsMatchOnlyMinors)!.Normalize(raw));
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData(" 20 ", "20")]
    [InlineData("007", "7")]
    [InlineData("0", null)]            // ниже границы (минимум 1)
    [InlineData("21", null)]           // выше границы (максимум 20)
    [InlineData("-3", null)]
    [InlineData("+3", null)]
    [InlineData("3.0", null)]
    [InlineData("3,0", null)]
    [InlineData("три", null)]
    [InlineData("١٢", null)]           // не ASCII-цифры
    [InlineData("9999999999", null)]   // слишком длинное
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_number_is_a_plain_non_negative_integer_within_the_bounds(string? raw, string? expected)
    {
        Assert.Equal(expected, SystemSettingDefinitions.Find(SystemSettingKeys.MaxActiveExchanges)!.Normalize(raw));
    }

    [Fact]
    public void Zero_days_of_cooldown_is_allowed_and_unknown_keys_are_not_found()
    {
        Assert.Equal("0", SystemSettingDefinitions.Find(SystemSettingKeys.DeclineCooldownDays)!.Normalize("0"));
        Assert.Equal("365", SystemSettingDefinitions.Find(SystemSettingKeys.DeclineCooldownDays)!.Normalize("365"));
        Assert.Null(SystemSettingDefinitions.Find(SystemSettingKeys.DeclineCooldownDays)!.Normalize("366"));
        Assert.Null(SystemSettingDefinitions.Find("Nope"));
        Assert.Null(SystemSettingDefinitions.Find(null));
    }
}
