using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using SkillSwap.Api.Auth;
using SkillSwap.Api.Middleware;
using SkillSwap.Api.OpenApi;
using SkillSwap.Application;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    // Enum в JSON — строками ("User", "Admin"), как и в БД (раздел 13 ТЗ), а не числами.
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<FrontendOptions>(builder.Configuration.GetSection(FrontendOptions.SectionName));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);

// CORS: refresh-токен едет в cookie, поэтому нужен AllowCredentials и точный адрес фронтенда (не "*").
var frontendOrigin = builder.Configuration[$"{FrontendOptions.SectionName}:BaseUrl"] ?? new FrontendOptions().BaseUrl;
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(frontendOrigin.TrimEnd('/'))
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

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
}

app.UseHttpsRedirection();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Нужен для WebApplicationFactory в интеграционных тестах.
public partial class Program;
