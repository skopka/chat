using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Skopka.Chat.Persistence.PostgreSql.Migrations;

/// <summary>Creates the protocol-v1 public device directory and ciphertext queue.</summary>
[DbContext(typeof(ChatDbContext))]
[Migration("202608310001_InitialEncryptedChatStorage")]
public sealed class InitialEncryptedChatStorage : Migration
{
    private static readonly string[] ConversationUserColumns = ["first_user_id", "second_user_id"];
    private static readonly string[] DeviceKeyColumns = ["device_id", "key_id"];
    private static readonly string[] PendingDeliveryColumns =
        ["recipient_device_id", "acknowledged_at", "expires_at", "accepted_at"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "conversations",
            columns: table => new
            {
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                first_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                second_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_conversations", item => item.conversation_id);
                table.CheckConstraint("ck_conversations_distinct_users", "first_user_id <> second_user_id");
            });

        migrationBuilder.CreateTable(
            name: "devices",
            columns: table => new
            {
                device_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                key_id = table.Column<Guid>(type: "uuid", nullable: false),
                encryption_public_key = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                signing_public_key = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_devices", item => item.device_id);
                table.CheckConstraint("ck_devices_encryption_key_size", "octet_length(encryption_public_key) = 32");
                table.CheckConstraint("ck_devices_revocation_time", "revoked_at IS NULL OR revoked_at >= registered_at");
                table.CheckConstraint("ck_devices_signing_key_size", "octet_length(signing_public_key) = 32");
            });

        migrationBuilder.CreateTable(
            name: "envelopes",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                protocol_version = table.Column<int>(type: "integer", nullable: false),
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                sender_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                recipient_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                sender_signing_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                recipient_encryption_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ephemeral_public_key = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                nonce = table.Column<byte[]>(type: "bytea", maxLength: 24, nullable: false),
                ciphertext = table.Column<byte[]>(type: "bytea", maxLength: 65536, nullable: false),
                authentication_tag = table.Column<byte[]>(type: "bytea", maxLength: 16, nullable: false),
                signature = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: false),
                canonical_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_envelopes", item => item.message_id);
                table.CheckConstraint("ck_envelopes_ciphertext_size", "octet_length(ciphertext) <= 65536");
                table.CheckConstraint("ck_envelopes_ephemeral_key_size", "octet_length(ephemeral_public_key) = 32");
                table.CheckConstraint("ck_envelopes_expiry", "expires_at IS NULL OR expires_at > sent_at");
                table.CheckConstraint("ck_envelopes_hash_size", "octet_length(canonical_hash) = 32");
                table.CheckConstraint("ck_envelopes_nonce_size", "octet_length(nonce) = 24");
                table.CheckConstraint("ck_envelopes_protocol_version", "protocol_version = 1");
                table.CheckConstraint("ck_envelopes_signature_size", "octet_length(signature) = 64");
                table.CheckConstraint("ck_envelopes_tag_size", "octet_length(authentication_tag) = 16");
                table.ForeignKey("fk_envelopes_conversations", item => item.conversation_id, "conversations", "conversation_id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("fk_envelopes_recipient_device", item => item.recipient_device_id, "devices", "device_id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("fk_envelopes_sender_device", item => item.sender_device_id, "devices", "device_id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("ix_conversations_users", "conversations", ConversationUserColumns);
        migrationBuilder.CreateIndex("ix_devices_user_id", "devices", "user_id");
        migrationBuilder.CreateIndex("ux_devices_device_key", "devices", DeviceKeyColumns, unique: true);
        migrationBuilder.CreateIndex("ix_envelopes_conversation_id", "envelopes", "conversation_id");
        migrationBuilder.CreateIndex("ix_envelopes_expires_at", "envelopes", "expires_at");
        migrationBuilder.CreateIndex("ix_envelopes_recipient_device_id", "envelopes", "recipient_device_id");
        migrationBuilder.CreateIndex("ix_envelopes_sender_device_id", "envelopes", "sender_device_id");
        migrationBuilder.CreateIndex(
            "ix_envelopes_pending_delivery",
            "envelopes",
            PendingDeliveryColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("envelopes");
        migrationBuilder.DropTable("conversations");
        migrationBuilder.DropTable("devices");
    }
}
