using System.Text.RegularExpressions;

namespace SkillSwap.Application.Chat;

/// <summary>
/// Антифрод чата (раздел 7.1 ТЗ): находит в тексте номера банковских карт и призывы к оплате, чтобы показать предупреждение
/// о запрете денежных расчётов между участниками. Сообщение не блокируется: ложные срабатывания неизбежны, поэтому это подсказка,
/// а не запрет. Проверка идёт ДО шифрования: после него текст сервер не читает.
/// </summary>
public static partial class PaymentContentDetector
{
    // 13–19 цифр подряд, допускаются пробелы и дефисы между ними (так пишут номера карт).
    [GeneratedRegex(@"(?<!\d)(?:\d[ \-]?){12,18}\d(?!\d)")]
    private static partial Regex CardCandidate();

    // Призывы к оплате и переводу на русском, таджикском и английском, а также платёжные системы и криптовалюты.
    [GeneratedRegex(
        @"перевед(и|ите)\s+(мне\s+)?(деньги|средства|на)|перевод\s+на\s+(карт|счёт|счет|телефон)|скин(ь|ьте)\s+(мне\s+)?(деньги|на)|кин(ь|ьте)\s+на\s+карт|на\s+мою\s+карту|номер\s+(моей\s+)?карты|оплат(и|ите|ой|у|ы)\b|заплат(и|ите)|" +
        @"пул\s+(диҳед|гузаронед|фиристед)|пардохт\s+кун|ба\s+корт|рақами\s+корт|" +
        @"\bpay\s+me\b|send\s+(me\s+)?(the\s+)?(money|cash|payment)|bank\s+transfer|wire\s+transfer|card\s+number|\biban\b|paypal|venmo|western\s+union|\bbitcoin\b|\busdt\b|\bcrypto\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PaymentPhrase();

    /// <summary>Есть ли в тексте номер карты (с проверкой контрольной суммы Луна) или призыв к оплате.</summary>
    public static bool LooksLikePayment(string text) =>
        !string.IsNullOrWhiteSpace(text) && (ContainsCardNumber(text) || PaymentPhrase().IsMatch(text));

    public static bool ContainsCardNumber(string text)
    {
        foreach (Match match in CardCandidate().Matches(text))
        {
            var digits = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }
}
