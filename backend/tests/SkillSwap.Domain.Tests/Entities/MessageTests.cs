using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Сообщение чата (раздел 7.1 ТЗ): правка и удаление автором в течение 15 минут, прежний текст сохраняется.</summary>
public class MessageTests
{
    private static readonly DateTime Sent = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Exchange = Guid.NewGuid();
    private static readonly Guid Author = Guid.NewGuid();

    private static Message New() => Persisted(Message.CreateText(Exchange, Author, "cipher-1", false));

    /// <summary>CreatedAt в приложении проставляет AppDbContext при сохранении; здесь делаем то же вручную.</summary>
    private static Message Persisted(Message message)
    {
        message.CreatedAt = Sent;
        return message;
    }

    [Fact]
    public void A_new_text_message_has_the_expected_state()
    {
        var m = New();

        Assert.Equal(MessageType.Text, m.Type);
        Assert.Equal(Author, m.SenderId);
        Assert.Equal("cipher-1", m.EncryptedContent);
        Assert.False(m.IsDeleted);
        Assert.False(m.PaymentWarning);
        Assert.Null(m.ReadAt);
    }

    [Fact]
    public void An_empty_ciphertext_is_refused() =>
        Assert.Equal("message_empty", Assert.Throws<DomainValidationException>(() => Message.CreateText(Exchange, Author, "", false)).Code);

    [Fact]
    public void The_edit_window_is_exactly_15_minutes()
    {
        var m = New();

        Assert.True(m.IsWithinEditWindow(Sent.AddMinutes(15)));
        Assert.False(m.IsWithinEditWindow(Sent.AddMinutes(15).AddTicks(1)));
    }

    [Fact]
    public void Editing_stores_the_previous_text_in_a_revision()
    {
        var m = New();

        var revision = m.Edit(Author, "cipher-2", true, textChanged: true, Sent.AddMinutes(5));

        Assert.NotNull(revision);
        Assert.Equal("cipher-1", revision!.PreviousEncryptedContent);
        Assert.Equal(m.Id, revision.MessageId);
        Assert.Equal(Sent.AddMinutes(5), revision.ChangedAt);
        Assert.Equal("cipher-2", m.EncryptedContent);
        Assert.Equal(Sent.AddMinutes(5), m.EditedAt);
        Assert.True(m.PaymentWarning);
    }

    [Fact]
    public void An_unchanged_text_makes_no_revision_and_no_edit_mark()
    {
        var m = New();

        Assert.Null(m.Edit(Author, "cipher-1", false, textChanged: false, Sent.AddMinutes(1)));
        Assert.Null(m.EditedAt);
    }

    [Fact]
    public void Deleting_keeps_the_text_for_the_moderator_and_marks_the_time()
    {
        var m = New();

        var revision = m.Delete(Author, Sent.AddMinutes(2));

        Assert.True(m.IsDeleted);
        Assert.Equal(Sent.AddMinutes(2), m.DeletedAt);
        Assert.Equal("cipher-1", revision.PreviousEncryptedContent);
        Assert.Equal("cipher-1", m.EncryptedContent);   // в базе текст остаётся; участникам его не отдают
    }

    [Fact]
    public void Nobody_but_the_author_can_change_it()
    {
        var m = New();

        Assert.Equal("not_message_author", Assert.Throws<DomainValidationException>(() => m.Edit(Guid.NewGuid(), "x", false, true, Sent)).Code);
        Assert.Equal("not_message_author", Assert.Throws<DomainValidationException>(() => m.Delete(Guid.NewGuid(), Sent)).Code);
    }

    [Fact]
    public void After_the_window_or_after_deletion_nothing_can_be_changed()
    {
        var late = New();
        Assert.Equal("edit_window_expired", Assert.Throws<DomainValidationException>(() => late.Edit(Author, "x", false, true, Sent.AddMinutes(16))).Code);
        Assert.Equal("edit_window_expired", Assert.Throws<DomainValidationException>(() => late.Delete(Author, Sent.AddMinutes(16))).Code);

        var deleted = New();
        deleted.Delete(Author, Sent.AddMinutes(1));
        Assert.Equal("message_deleted", Assert.Throws<DomainValidationException>(() => deleted.Edit(Author, "x", false, true, Sent.AddMinutes(2))).Code);
        Assert.Equal("message_deleted", Assert.Throws<DomainValidationException>(() => deleted.Delete(Author, Sent.AddMinutes(2))).Code);
    }

    [Fact]
    public void Reading_keeps_the_first_time()
    {
        var m = New();

        m.MarkRead(Sent.AddMinutes(1));
        m.MarkRead(Sent.AddMinutes(9));

        Assert.Equal(Sent.AddMinutes(1), m.ReadAt);
    }
}
