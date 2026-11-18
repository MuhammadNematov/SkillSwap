using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NewMessagesNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExchangeId",
                table: "Notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_ExchangeId",
                table: "Notifications",
                columns: new[] { "UserId", "ExchangeId" },
                unique: true,
                filter: "\"Type\" = 'NewMessages' AND NOT \"IsRead\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_UserId_ExchangeId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "ExchangeId",
                table: "Notifications");
        }
    }
}
