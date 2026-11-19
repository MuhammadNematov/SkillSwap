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

            // Для свойства `CaseDecision?` генератор добавляет в список значений общей схемы null: значение «нет» выражается в самом свойстве (oneOf с null), а не в перечислении.
            if (schema.Enum is { } values && values.Any(v => v is null))
            {
                schema.Enum = values.Where(v => v is not null).ToList();
            }
        }

        return Task.CompletedTask;
    }
}
