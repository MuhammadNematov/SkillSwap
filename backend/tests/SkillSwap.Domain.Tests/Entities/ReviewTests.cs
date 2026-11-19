using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Отзыв (раздел 8.1 ТЗ): оценки, текст, слепая публикация, спор и скрытие.</summary>
public class ReviewTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Exchange = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly string GoodText = "Хороший преподаватель, всё объяснил понятно.";

    private static Review New(int k = 5, int e = 4, int p = 3, int po = 4, string? text = null, bool full = false) =>
        Review.Create(Exchange, A, B, k, e, p, po, text ?? GoodText, full, Now);

    // ---------- создание ----------

    [Fact]
    public void A_new_review_is_pending_hidden_from_everyone_and_not_in_the_rating()
    {
        var r = New(full: true);

        Assert.Equal(ReviewStatus.Pending, r.Status);
        Assert.Null(r.PublishedAt);
        Assert.False(r.IsVisible);
        Assert.False(r.CountsInRating);
        Assert.True(r.IsFullExchange);
        Assert.Equal(A, r.AuthorId);
        Assert.Equal(B, r.TargetId);
    }

    [Fact]
    public void The_weight_of_a_review_is_the_mean_of_its_four_scores()
    {
        Assert.Equal(4m, New(5, 4, 3, 4).Average);
        Assert.Equal(1m, New(1, 1, 1, 1).Average);
        Assert.Equal(4.75m, New(5, 5, 5, 4).Average);
    }

    [Theory]
    [InlineData(0, 3, 3, 3)]
    [InlineData(3, 6, 3, 3)]
    [InlineData(3, 3, -1, 3)]
    [InlineData(3, 3, 3, 0)]
    public void Every_score_must_be_between_one_and_five(int k, int e, int p, int po)
    {
        var error = Assert.Throws<DomainValidationException>(() => New(k, e, p, po));

        Assert.Equal("review_score_out_of_range", error.Code);
    }

    [Fact]
    public void The_scores_one_and_five_are_valid()
    {
        New(1, 5, 1, 5);
    }

    [Fact]
    public void The_text_is_trimmed_and_must_have_twenty_to_a_thousand_characters()
    {
        Assert.Equal("review_text_too_short", Assert.Throws<DomainValidationException>(() => New(text: new string('а', 19))).Code);
        Assert.Equal("review_text_too_short", Assert.Throws<DomainValidationException>(() => New(text: "  " + new string('а', 19) + "  ")).Code);   // пробелы не считаются
        Assert.Equal("review_text_too_short", Assert.Throws<DomainValidationException>(() => Review.Create(Exchange, A, B, 5, 5, 5, 5, null, false, Now)).Code);
        Assert.Equal("review_text_too_long", Assert.Throws<DomainValidationException>(() => New(text: new string('а', 1001))).Code);

        Assert.Equal(new string('а', 20), New(text: "  " + new string('а', 20) + "\n").Text);
        Assert.Equal(1000, New(text: new string('б', 1000)).Text.Length);
    }

    [Fact]
    public void Nobody_reviews_themselves()
    {
        Assert.Equal("review_self", Assert.Throws<DomainValidationException>(() => Review.Create(Exchange, A, A, 5, 5, 5, 5, GoodText, false, Now)).Code);
    }

    [Fact]
    public void The_write_window_is_fourteen_days_and_the_blind_window_is_seven()
    {
        Assert.Equal(TimeSpan.FromDays(14), Review.WriteWindow);
        Assert.Equal(TimeSpan.FromDays(7), Review.BlindWindow);
    }

    // ---------- срок публикации ----------

    [Fact]
    public void A_review_is_due_for_publication_seven_days_after_it_was_written_and_not_earlier()
    {
        var r = New();

        Assert.Equal(Now.AddDays(7), r.PublishDueAt);
        Assert.False(r.IsDueForPublication(Now.AddDays(7).AddTicks(-1)));
        Assert.True(r.IsDueForPublication(Now.AddDays(7)));
        Assert.True(r.IsDueForPublication(Now.AddDays(30)));
    }

    [Fact]
    public void Editing_does_not_move_the_publication_time()
    {
        var r = New();

        r.Edit(2, 2, 2, 2, "Переписал отзыв, теперь он честнее и подробнее.");

        Assert.Equal(Now.AddDays(7), r.PublishDueAt);
    }

    [Fact]
    public void A_published_review_is_never_due_again()
    {
        var r = New();
        r.Publish(Now.AddDays(1));

        Assert.False(r.IsDueForPublication(Now.AddDays(30)));
    }

    // ---------- правка и публикация ----------

    [Fact]
    public void An_unpublished_review_can_be_edited_and_a_published_one_cannot()
    {
        var r = New();

        r.Edit(2, 2, 2, 2, "Передумал, уроки были слабыми и сумбурными.");
        Assert.Equal(2, r.ScoreKnowledge);
        Assert.Equal("Передумал, уроки были слабыми и сумбурными.", r.Text);

        r.Publish(Now);
        Assert.Equal("review_already_published", Assert.Throws<DomainValidationException>(() => r.Edit(5, 5, 5, 5, GoodText)).Code);
        Assert.Equal(2, r.ScoreKnowledge);   // не изменился
    }

    [Fact]
    public void An_edit_is_validated_like_a_new_review_and_a_bad_one_changes_nothing()
    {
        var r = New();

        Assert.Throws<DomainValidationException>(() => r.Edit(9, 5, 5, 5, GoodText));
        Assert.Throws<DomainValidationException>(() => r.Edit(5, 5, 5, 5, "коротко"));

        Assert.Equal(5, r.ScoreKnowledge);
        Assert.Equal(GoodText, r.Text);
    }

    [Fact]
    public void Publication_makes_the_review_visible_and_counted_once()
    {
        var r = New();

        Assert.True(r.Publish(Now));

        Assert.Equal(ReviewStatus.Published, r.Status);
        Assert.Equal(Now, r.PublishedAt);
        Assert.True(r.IsVisible);
        Assert.True(r.CountsInRating);
        Assert.False(r.Publish(Now.AddDays(3)));            // повтор ничего не меняет
        Assert.Equal(Now, r.PublishedAt);
    }

    // ---------- спор ----------

    [Fact]
    public void A_dispute_freezes_the_review_visible_but_out_of_the_rating_and_keeping_it_brings_it_back()
    {
        var r = New();
        r.Publish(Now);

        r.Freeze();
        Assert.Equal(ReviewStatus.Frozen, r.Status);
        Assert.True(r.IsVisible);
        Assert.False(r.CountsInRating);

        r.Unfreeze();
        Assert.Equal(ReviewStatus.Published, r.Status);
        Assert.True(r.CountsInRating);
    }

    [Fact]
    public void Hiding_works_from_published_and_frozen_and_the_hidden_review_is_invisible_and_uncounted()
    {
        var published = New();
        published.Publish(Now);
        published.Hide();

        var frozen = New();
        frozen.Publish(Now);
        frozen.Freeze();
        frozen.Hide();

        foreach (var r in new[] { published, frozen })
        {
            Assert.Equal(ReviewStatus.Hidden, r.Status);
            Assert.False(r.IsVisible);
            Assert.False(r.CountsInRating);
        }
    }

    [Fact]
    public void A_hidden_review_comes_back_only_by_restore_after_an_appeal()
    {
        var r = New();
        r.Publish(Now);
        r.Hide();

        r.Restore();

        Assert.Equal(ReviewStatus.Published, r.Status);
        Assert.True(r.CountsInRating);
    }

    [Fact]
    public void A_pending_review_cannot_be_frozen_hidden_or_restored()
    {
        Assert.Throws<InvalidStateTransitionException>(() => New().Freeze());
        Assert.Throws<InvalidStateTransitionException>(() => New().Hide());
        Assert.Throws<InvalidStateTransitionException>(() => New().Unfreeze());
        Assert.Throws<InvalidStateTransitionException>(() => New().Restore());
    }

    [Fact]
    public void Illegal_moves_between_published_frozen_and_hidden_are_refused()
    {
        var published = New();
        published.Publish(Now);
        Assert.Throws<InvalidStateTransitionException>(() => published.Unfreeze());   // не заморожен

        var hidden = New();
        hidden.Publish(Now);
        hidden.Hide();
        Assert.Throws<InvalidStateTransitionException>(() => hidden.Freeze());
        Assert.Throws<InvalidStateTransitionException>(() => hidden.Hide());
        Assert.Throws<InvalidStateTransitionException>(() => hidden.Unfreeze());

        var frozen = New();
        frozen.Publish(Now);
        frozen.Freeze();
        Assert.Throws<InvalidStateTransitionException>(() => frozen.Freeze());
        Assert.Throws<InvalidStateTransitionException>(() => frozen.Restore());
    }

    [Fact]
    public void A_frozen_or_hidden_review_cannot_be_edited()
    {
        var frozen = New();
        frozen.Publish(Now);
        frozen.Freeze();

        Assert.Equal("review_already_published", Assert.Throws<DomainValidationException>(() => frozen.Edit(1, 1, 1, 1, GoodText)).Code);
    }
}
