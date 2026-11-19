using Microsoft.Extensions.Options;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Api;

/// <summary>Применяет настройки сроков урока (секция Lessons) при старте. Вне Development и Testing допустимы только значения ТЗ.</summary>
public static class LessonTimingSetup
{
    public static void Apply(IServiceProvider services, IHostEnvironment environment)
    {
        var options = services.GetRequiredService<IOptions<LessonTimingOptions>>().Value;
        if (options.Validate() is { } error)
        {
            throw new InvalidOperationException(error);
        }

        if (!options.IsDefault && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                "Lessons:* отличаются от значений ТЗ: сроки урока можно менять только в Development и Testing (для ручной проверки видеокомнаты).");
        }

        // Каждый запуск приложения выставляет сроки заново: значения предыдущего запуска (например, другого хоста в тестах) не «залипают».
        LessonTiming.Configure(options.ToSettings());
    }
}
