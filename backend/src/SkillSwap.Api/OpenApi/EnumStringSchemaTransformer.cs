using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SkillSwap.Api.OpenApi;

/// <summary>
/// Перечисления в JSON идут строками, поэтому в схеме у каждого перечисления явно <c>type: string</c> и список значений.
/// Само по себе включение строкового конвертера даёт только список (<c>enum</c>) без типа: генераторы клиентов на фронтенде различают такие схемы плохо.
/// </summary>
internal sealed class EnumStringSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (type.IsEnum)
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = null;
        }

        return Task.CompletedTask;
    }
}
