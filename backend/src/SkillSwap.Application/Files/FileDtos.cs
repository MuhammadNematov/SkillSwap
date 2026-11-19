using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Files;

/// <summary>Вложение сообщения: сам файл скачивается по подписанной ссылке (<c>GET /attachments/{id}/url</c>).</summary>
public sealed record AttachmentDto(Guid Id, AttachmentKind Kind, string FileName, string ContentType, long SizeBytes, int? DurationSeconds);

/// <param name="Url">Подписанная ссылка: действует 5 минут, браузер сохраняет файл под его именем.</param>
public sealed record AttachmentUrlDto(string Url, DateTime ExpiresAt, string FileName);

/// <summary>Загружаемый файл: поток читается один раз.</summary>
public sealed record UploadedFile(string? FileName, long Length, Stream Content);

/// <summary>Результат загрузки аватара.</summary>
public sealed record AvatarDto(string AvatarUrl);

public static class AttachmentMapper
{
    public static AttachmentDto ToDto(Domain.Entities.Attachment a) =>
        new(a.Id, a.Kind, a.FileName, a.MimeType, a.SizeBytes, a.DurationSec);
}
