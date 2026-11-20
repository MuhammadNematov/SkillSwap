using System.Linq.Expressions;

namespace SkillSwap.Application.Abstractions;

/// <summary>
/// Фоновые задачи (раздел 11.1 ТЗ: Hangfire с хранением в PostgreSQL). Задача описывается вызовом метода сервиса:
/// <c>jobs.Enqueue&lt;ReminderJobs&gt;(j =&gt; j.SendAsync(sessionId))</c>. Hangfire сохраняет вызов и аргументы в БД и выполняет его
/// в другом потоке (при сбое повторяет). Поэтому:
/// <list type="bullet">
/// <item>аргументы только простые и небольшие (id, строки), без секретов открытым текстом: они лежат в БД;</item>
/// <item>каждая задача идемпотентна: перед действием проверяет текущее состояние, повторный запуск ничего не портит;</item>
/// <item>метод должен быть публичным, а класс зарегистрирован в DI (создаётся на каждый запуск).</item>
/// </list>
/// </summary>
public interface IBackgroundJobs
{
    /// <summary>Выполнить как можно скорее.</summary>
    void Enqueue<T>(Expression<Func<T, Task>> job) where T : notnull;

    /// <summary>Выполнить не раньше указанного момента.</summary>
    void Schedule<T>(Expression<Func<T, Task>> job, DateTimeOffset runAt) where T : notnull;

    /// <summary>
    /// Повторять по расписанию (cron, UTC). Повторная регистрация с тем же <paramref name="id"/> обновляет расписание, а не плодит копии. Такие задачи — страховка
    /// для разовых: они сверяются с состоянием и делают то, что разовая задача не успела (например, Hangfire был выключен в нужный момент).
    /// </summary>
    void Recurring<T>(string id, Expression<Func<T, Task>> job, string cron) where T : notnull;
}
