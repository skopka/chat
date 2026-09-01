using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skopka.Chat.Attachments.PostgreSql.Migrations;

/// <inheritdoc />
[DbContext(typeof(AttachmentDbContext))]
[Migration("202609010003_InitialEncryptedAttachmentStorage")]
public sealed class InitialEncryptedAttachmentStorage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "chat_attachments",
            columns: table => new
            {
                attachment_id = table.Column<Guid>(type: "uuid", nullable: false),
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                uploader_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                ciphertext_length = table.Column<long>(type: "bigint", nullable: false),
                ciphertext_sha256 = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_chat_attachments", x => x.attachment_id);
                table.CheckConstraint("ck_chat_attachments_ciphertext_length", "ciphertext_length > 0");
                table.CheckConstraint("ck_chat_attachments_ciphertext_size", "octet_length(ciphertext) = ciphertext_length");
                table.CheckConstraint("ck_chat_attachments_expiry", "expires_at IS NULL OR expires_at > created_at");
                table.CheckConstraint("ck_chat_attachments_hash_size", "octet_length(ciphertext_sha256) = 32");
            });

        migrationBuilder.CreateIndex(
            name: "ix_chat_attachments_conversation_id",
            table: "chat_attachments",
            column: "conversation_id");

        migrationBuilder.CreateIndex(
            name: "ix_chat_attachments_expires_at",
            table: "chat_attachments",
            column: "expires_at");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "chat_attachments");
}
