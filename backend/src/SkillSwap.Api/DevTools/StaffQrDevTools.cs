using System.Net;
using System.Text;
using Microsoft.AspNetCore.Identity;
using QRCoder;
using SkillSwap.Application.Auth;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.DevTools;

/// <summary>
/// <c>GET /dev/staff-qr</c>: страница для разработки со всеми dev-сотрудниками (модераторы, админ, суперадмин): пароль, ключ TOTP, QR-код для Google Authenticator и текущий код.
/// Маршрут существует только в Development (вне него 404). При первом открытии недостающие dev-аккаунты создаются, как у <c>POST /dev/seed-staff</c>:
/// пароль <see cref="CaseSeedDevTools.StaffPassword"/>, 2FA включена с читаемым ключом.
/// </summary>
public static class StaffQrDevTools
{
    private const string Issuer = "SkillSwap";

    public static IEndpointRouteBuilder MapStaffQrDevTools(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dev/staff-qr", StaffQrAsync).ExcludeFromDescription();
        return app;
    }

    private static readonly (string Email, string Name, UserRole Role)[] Accounts =
    [
        (CaseSeedDevTools.SuperAdminEmail, "Суперадмин (dev)", UserRole.SuperAdmin),
        (CaseSeedDevTools.AdminEmail, "Админ (dev)", UserRole.Admin),
        (CaseSeedDevTools.ModeratorEmail, "Модератор (dev)", UserRole.Moderator),
        (CaseSeedDevTools.SecondModeratorEmail, "Модератор 2 (dev)", UserRole.Moderator),
    ];

    private static async Task<IResult> StaffQrAsync(HttpContext http, UserManager<User> users)
    {
        var cards = new StringBuilder();
        foreach (var (email, name, role) in Accounts)
        {
            var staff = await CaseSeedDevTools.EnsureStaffAsync(http, email, name, role);
            cards.Append(Card(staff.User, staff.Key));
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.Content(Page(cards.ToString()), "text/html; charset=utf-8");
    }

    /// <summary>Содержимое QR-кода: то же otpauth://, что отдаёт включение 2FA (<c>authenticatorUri</c>).</summary>
    internal static string OtpAuthUri(string email, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(email)}?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";

    private static string QrSvg(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(5, "#17143d", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    private static string Card(User user, string key)
    {
        var email = WebUtility.HtmlEncode(user.Email ?? string.Empty);
        var uri = OtpAuthUri(user.Email ?? string.Empty, key);
        var spaced = string.Join(' ', Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));
        return $$"""
            <section class="card" data-email="{{email}}">
              <h2>{{WebUtility.HtmlEncode(user.Name)}} <span class="role">{{user.Role}}</span></h2>
              <div class="qr">{{QrSvg(uri)}}</div>
              <dl>
                <dt>Почта</dt><dd>{{email}}</dd>
                <dt>Пароль</dt><dd><code>{{WebUtility.HtmlEncode(CaseSeedDevTools.StaffPassword)}}</code></dd>
                <dt>Ключ</dt><dd><code class="key">{{WebUtility.HtmlEncode(spaced)}}</code></dd>
                <dt>Код сейчас</dt><dd><code class="code" data-code>······</code> <span class="left" data-left></span></dd>
              </dl>
              <p class="uri"><a href="{{WebUtility.HtmlEncode(uri)}}">otpauth://</a> (открыть в приложении на этом устройстве)</p>
            </section>
            """;
    }

    private static string Page(string cards) => $$"""
        <!doctype html>
        <html lang="ru">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>Dev: QR-коды 2FA сотрудников</title>
        <style>
          body { font: 15px/1.45 system-ui, sans-serif; margin: 0; background: #f8f7ff; color: #17143d; }
          main { max-width: 1100px; margin: 0 auto; padding: 24px 16px 48px; }
          h1 { font-size: 24px; margin: 0 0 6px; }
          .warn { background: #fef3c7; color: #92400e; border-radius: 10px; padding: 10px 14px; margin: 12px 0 20px; }
          .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(310px, 1fr)); gap: 16px; }
          .card { background: #fff; border: 1px solid #ece9f7; border-radius: 12px; padding: 16px; }
          .card h2 { font-size: 16px; margin: 0 0 10px; display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
          .role { font-size: 12px; background: #eee9ff; color: #4a2a9a; border-radius: 999px; padding: 2px 10px; }
          .qr { width: 100%; max-width: 220px; margin: 0 auto 12px; }
          .qr svg { width: 100%; height: auto; display: block; }
          dl { margin: 0; display: grid; grid-template-columns: auto 1fr; gap: 4px 12px; }
          dt { color: #5b5878; }
          dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
          code { font: 13px ui-monospace, Menlo, monospace; }
          .code { font-size: 18px; font-weight: 700; letter-spacing: 2px; }
          .left { color: #5b5878; font-size: 12px; }
          .uri { font-size: 12px; color: #5b5878; margin: 10px 0 0; }
        </style>
        </head>
        <body>
        <main>
          <h1>QR-коды 2FA dev-сотрудников</h1>
          <p>Отсканируйте QR в Google Authenticator (или введите ключ вручную), дальше вход сотрудника: почта, пароль и код из приложения.</p>
          <p class="warn">Только для разработки: страница есть лишь при ASPNETCORE_ENVIRONMENT=Development, вне неё маршрута нет (404). Не публикуйте её и не открывайте наружу.</p>
          <div class="grid">
        {{cards}}
          </div>
        </main>
        <script>
          async function refresh() {
            for (const card of document.querySelectorAll('[data-email]')) {
              try {
                const r = await fetch('/dev/staff-code', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: card.dataset.email }) });
                if (!r.ok) continue;
                const j = await r.json();
                card.querySelector('[data-code]').textContent = j.codes[0].replace(/(\d{3})(\d{3})/, '$1 $2');
                card.querySelector('[data-left]').textContent = 'ещё ' + j.secondsLeft + ' с';
              } catch { /* страница для разработки: ошибки сети не важны */ }
            }
          }
          refresh();
          setInterval(refresh, 1000);
        </script>
        </body>
        </html>
        """;
}
