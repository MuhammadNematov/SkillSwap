using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkillSwap.Api.Middleware;
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
    public async Task UnknownException_gives_500_without_leaking_details()
    {
        var (status, body) = await HandleAsync(new InvalidOperationException("секрет внутри"));

        Assert.Equal(500, status);
        Assert.Equal("internal_error", body.GetProperty("code").GetString());
        Assert.DoesNotContain("секрет", body.GetRawText());
    }
}
