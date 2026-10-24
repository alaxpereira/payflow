using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayFlow.Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxPublishAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "error",
                table: "outbox_messages",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attempts",
                table: "outbox_messages");

            migrationBuilder.AlterColumn<string>(
                name: "error",
                table: "outbox_messages",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);
        }
    }
}
