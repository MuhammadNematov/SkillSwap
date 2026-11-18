using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using SkillSwap.Api.Auth;
using SkillSwap.Api.Captcha;
using SkillSwap.Api.DevTools;
using SkillSwap.Api.Middleware;
using SkillSwap.Api.OpenApi;
using SkillSwap.Api.RateLimiting;
using SkillSwap.Application;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure;
using SkillSwap.Infrastructure.Jobs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    // Enum в JSON — строками ("User", "Admin"), как и в БД (раздел 13 ТЗ), а не числами.
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<FrontendOptions>(builder.Configuration.GetSection(FrontendOptions.SectionName));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddRateLimiting(builder.Configuration);
builder.Services.AddScoped<TurnstileFilter>();

// Хаб уведомлений (SignalR): пользователь — клейм sub; доставка из фоновых задач.
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));   // перечисления строками, как в REST
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, SkillSwap.Api.Hubs.SubUserIdProvider>();
builder.Services.AddScoped<SkillSwap.Application.Notifications.INotificationPusher, SkillSwap.Api.Hubs.SignalRNotificationPusher>();

// CORS: refresh-токен едет в cookie, поэтому нужен AllowCredentials и точный адрес фронтенда (не "*").
var frontendOrigin = builder.Configuration[$"{FrontendOptions.SectionName}:BaseUrl"] ?? new FrontendOptions().BaseUrl;
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(frontendOrigin.TrimEnd('/'))
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()
    // Фронтенд должен видеть, через сколько повторить запрос после 429.
    .WithExposedHeaders("Retry-After")));

// Ошибки — ProblemDetails, обработка в одном месте (раздел 11.1 ТЗ).
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

var app = builder.Build();

app.UseExceptionHandler();

// Документация API (договор с фронтендом) — только в разработке.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
    app.MapLiveKitDevTools();   // тестовая страница и выдача токена: только в Development
}

// В Development LiveKit (в Docker) шлёт webhook по обычному http на localhost; редирект на https с самоподписанным
// сертификатом его бы сломал. Для остальных путей и сред редирект работает как обычно.
if (app.Environment.IsDevelopment())
{
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/api/v1/webhooks"),
        branch => branch.UseHttpsRedirection());
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseAuthentication();
app.UseConfiguredRateLimiter();   // после UseAuthentication: общий лимит считается по авторизованному пользователю
app.UseAuthorization();
app.UseDevelopmentHangfireDashboard();   // панель фоновых задач: только в Development

app.MapControllers();
app.MapHub<SkillSwap.Api.Hubs.NotificationsHub>("/hubs/notifications");

app.Run();

// Нужен для WebApplicationFactory в интеграционных тестах.
public partial class Program;
