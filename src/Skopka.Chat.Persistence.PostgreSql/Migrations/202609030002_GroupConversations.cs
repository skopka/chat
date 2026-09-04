using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Skopka.Chat.Persistence.PostgreSql.Migrations;

/// <summary>Adds server-visible small-group metadata and active membership.</summary>
[DbContext(typeof(ChatDbContext))]
[Migration("202609030002_GroupConversations")]
public sealed class GroupConversations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_conversations_distinct_users", "conversations");
        migrationBuilder.DropIndex("ux_conversations_users", "conversations");
        migrationBuilder.AlterColumn<Guid>(
            name: "first_user_id",
            table: "conversations",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");
        migrationBuilder.AlterColumn<Guid>(
            name: "second_user_id",
            table: "conversations",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");
        migrationBuilder.AddColumn<short>(
            name: "conversation_kind",
            table: "conversations",
            type: "smallint",
            nullable: false,
            defaultValue: (short)1);
        migrationBuilder.AddColumn<string>(
            name: "title",
            table: "conversations",
            type: "text",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "created_by_user_id",
            table: "conversations",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "revision",
            table: "conversations",
            type: "bigint",
            nullable: true);
        migrationBuilder.AddCheckConstraint(
            name: "ck_conversations_shape",
            table: "conversations",
            sql: "(conversation_kind = 1 AND first_user_id IS NOT NULL AND second_user_id IS NOT NULL AND first_user_id <> second_user_id AND title IS NULL AND created_by_user_id IS NULL AND revision IS NULL) OR " +
                 "(conversation_kind = 2 AND first_user_id IS NULL AND second_user_id IS NULL AND title IS NOT NULL AND octet_length(title) BETWEEN 1 AND 256 AND created_by_user_id IS NOT NULL AND revision >= 1)");
        migrationBuilder.CreateIndex(
            name: "ux_conversations_users",
            table: "conversations",
            columns: ["first_user_id", "second_user_id"],
            unique: true,
            filter: "conversation_kind = 1");

        migrationBuilder.CreateTable(
            name: "group_conversation_members",
            columns: table => new
            {
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<short>(type: "smallint", nullable: false),
                joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_group_conversation_members", item => new { item.conversation_id, item.user_id });
                table.CheckConstraint("ck_group_members_role", "role BETWEEN 1 AND 3");
                table.ForeignKey(
                    "fk_group_members_conversation",
                    item => item.conversation_id,
                    "conversations",
                    "conversation_id",
                    onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(
            name: "ix_group_members_user_conversation",
            table: "group_conversation_members",
            columns: ["user_id", "conversation_id"]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("group_conversation_members");
        migrationBuilder.DropCheckConstraint("ck_conversations_shape", "conversations");
        migrationBuilder.DropIndex("ux_conversations_users", "conversations");
        migrationBuilder.DropColumn("conversation_kind", "conversations");
        migrationBuilder.DropColumn("title", "conversations");
        migrationBuilder.DropColumn("created_by_user_id", "conversations");
        migrationBuilder.DropColumn("revision", "conversations");
        migrationBuilder.AlterColumn<Guid>(
            name: "first_user_id",
            table: "conversations",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);
        migrationBuilder.AlterColumn<Guid>(
            name: "second_user_id",
            table: "conversations",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);
        migrationBuilder.AddCheckConstraint(
            name: "ck_conversations_distinct_users",
            table: "conversations",
            sql: "first_user_id <> second_user_id");
        migrationBuilder.CreateIndex(
            name: "ux_conversations_users",
            table: "conversations",
            columns: ["first_user_id", "second_user_id"],
            unique: true);
    }
}
