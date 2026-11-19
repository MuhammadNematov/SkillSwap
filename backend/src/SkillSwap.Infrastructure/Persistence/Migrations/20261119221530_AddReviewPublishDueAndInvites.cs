using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewPublishDueAndInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PublishDueAt",
                table: "Reviews",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewInvitesSentAt",
                table: "Exchanges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewReminderSentAt",
                table: "Exchanges",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublishDueAt",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "ReviewInvitesSentAt",
                table: "Exchanges");

            migrationBuilder.DropColumn(
                name: "ReviewReminderSentAt",
                table: "Exchanges");
        }
    }
}
