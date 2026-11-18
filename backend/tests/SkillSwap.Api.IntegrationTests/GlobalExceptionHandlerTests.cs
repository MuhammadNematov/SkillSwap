using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkillSwap.Api.Middleware;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Api.IntegrationTests;

public class GlobalExceptionHandlerTests
{
    private static async Task<(int Status, JsonElement Body)> HandleAsync(Exception exception)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        var provider = services.BuildServiceProvider();

        var handler = new GlobalExceptionHandler(
            provider.GetRequiredService<IProblemDetailsService>(),
            NullLogger<GlobalExceptionHandler>.Instance);

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);
        Assert.True(handled);

        context.Response.Body.Position = 0;
        var body = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, body.RootElement);
    }

    [Fact]
    public async Task InvalidStateTransition_gives_409_with_code()
    {
        var (status, body) = await HandleAsync(new InvalidStateTransitionException("Exchange", "Completed", "Active"));

        Assert.Equal(409, status);
        Assert.Equal(409, body.GetProperty("status").GetInt32());
        Assert.Equal("invalid_state_transition", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConcurrencyConflict_gives_409()
    {
        var (status, body) = await HandleAsync(new DbUpdateConcurrencyException());

        Assert.Equal(409, status);
        Assert.Equal("concurrency_conflict", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConflictException_gives_409_with_its_code()
    {
        var (status, body) = await HandleAsync(new ConflictException("email_already_registered", "занят"));

        Assert.Equal(409, status);
        Assert.Equal("email_already_registered", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task BadRequestException_gives_400_with_its_code()
    {
        var (status, body) = await HandleAsync(new BadRequestException("invalid_confirmation_token", "плохая ссылка"));

        Assert.Equal(400, status);
        Assert.Equal("invalid_confirmation_token", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ValidationException_gives_400_with_error_codes_per_field()
    {
        var failures = new[]
        {
            new ValidationFailure("Password", "msg") { ErrorCode = "password_too_short" },
            new ValidationFailure("Password", "msg") { ErrorCode = "password_needs_digit" },
            new ValidationFailure("Email", "msg") { ErrorCode = "invalid_email" },
        };

        var (status, body) = await HandleAsync(new ValidationException(failures));

        Assert.Equal(400, status);
        Assert.Equal("validation_failed", body.GetProperty("code").GetString());
        var errors = body.GetProperty("errors");
        Assert.Equal(
            ["password_too_short", "password_needs_digit"],
            errors.GetProperty("password").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("invalid_email", errors.GetProperty("email")[0].GetString());
    }

    [Fact]
    public async Task UnknownException_gives_500_without_leaking_details()
    {
        var (status, body) = await HandleAsync(new InvalidOperationException("секрет внутри"));

        Assert.Equal(500, status);
        Assert.Equal("internal_error", body.GetProperty("code").GetString());
        Assert.DoesNotContain("секрет", body.GetRawText());
    }

    [Fact]
    public async Task An_error_can_add_its_own_fields_to_the_response()
    {
        var id = Guid.NewGuid();

        var (status, body) = await HandleAsync(new ConflictException("skill_already_exists", "уже есть",
            new Dictionary<string, object?> { ["existingSkillId"] = id }));

        Assert.Equal(409, status);
        Assert.Equal("skill_already_exists", body.GetProperty("code").GetString());
        Assert.Equal(id, body.GetProperty("existingSkillId").GetGuid());
    }

    [Fact]
    public async Task An_error_cannot_overwrite_the_standard_fields_of_the_response()
    {
        var (status, body) = await HandleAsync(new ConflictException("real_code", "detail",
            new Dictionary<string, object?> { ["code"] = "fake", ["status"] = 200, ["errors"] = "x", ["Code"] = "fake2", ["extra"] = 1 }));

        Assert.Equal(409, status);
        Assert.Equal("real_code", body.GetProperty("code").GetString());
        Assert.Equal(409, body.GetProperty("status").GetInt32());
        Assert.False(body.TryGetProperty("errors", out _));
        Assert.Equal(1, body.GetProperty("extra").GetInt32());
    }
}
