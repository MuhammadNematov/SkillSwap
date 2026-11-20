using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatPurgeAndOverdueMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ChatPurgedAt",
                table: "Exchanges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OverdueMarkedAt",
                table: "Cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_ClosedAt",
                table: "Exchanges",
                column: "ClosedAt",
                filter: "\"ChatPurgedAt\" IS NULL AND \"ClosedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Exchanges_ClosedAt",
                table: "Exchanges");

            migrationBuilder.DropColumn(
                name: "ChatPurgedAt",
                table: "Exchanges");

            migrationBuilder.DropColumn(
                name: "OverdueMarkedAt",
                table: "Cases");
        }
    }
}
