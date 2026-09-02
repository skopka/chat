using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Skopka.Chat.Persistence.PostgreSql.Migrations;

/// <summary>Canonicalizes participant order and enforces one conversation per unordered user pair.</summary>
[DbContext(typeof(ChatDbContext))]
[Migration("202609020004_UniquePersonalConversations")]
public sealed class UniquePersonalConversations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE conversations
            SET first_user_id = LEAST(first_user_id, second_user_id),
                second_user_id = GREATEST(first_user_id, second_user_id);
            """);
        migrationBuilder.DropIndex("ix_conversations_users", "conversations");
        migrationBuilder.CreateIndex(
            "ux_conversations_users",
            "conversations",
            ["first_user_id", "second_user_id"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("ux_conversations_users", "conversations");
        migrationBuilder.CreateIndex(
            "ix_conversations_users",
            "conversations",
            ["first_user_id", "second_user_id"]);
    }
}
