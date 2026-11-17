using Scalar.AspNetCore;
using SkillSwap.Api.Middleware;
using SkillSwap.Api.OpenApi;
using SkillSwap.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration);

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

app.UseAuthorization();

app.MapControllers();

app.Run();

// Нужен для WebApplicationFactory в интеграционных тестах.
public partial class Program;
