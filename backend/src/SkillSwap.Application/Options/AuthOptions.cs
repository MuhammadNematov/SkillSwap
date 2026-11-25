using System.ComponentModel.DataAnnotations;

namespace SkillSwap.Application.Options;

/// <summary>Настройки входа и обновления сеанса (секция <c>Auth</c>).</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Льготное окно после ротации refresh-токена, в секундах. Пока оно не прошло, повторное предъявление уже заменённого токена (вкладка не успела получить
    /// ответ, нетерпеливое обновление страницы, потерянный ответ) не считается кражей: возвращается тот же уже выданный преемник, цепочка не ветвится.
    /// После окна, или если преемник уже сам был использован, действует прежняя защита (отзыв всех сеансов). 0 выключает окно.
    /// </summary>
    [Range(0, 300)]
    public int RefreshGraceSeconds { get; set; } = 15;
}
