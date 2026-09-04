using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Skopka.Chat.Persistence.PostgreSql.Migrations;

/// <summary>Adds the durable lease-based outbox for encrypted-envelope acceptance events.</summary>
[DbContext(typeof(ChatDbContext))]
[Migration("202609040001_EncryptedEnvelopeEventOutbox")]
public sealed class EncryptedEnvelopeEventOutbox : Migration
{
    private static readonly string[] SourceColumns = ["source_message_id", "event_type", "event_version"];
    private static readonly string[] PendingColumns =
        ["published_at", "next_attempt_at", "lease_expires_at", "occurred_at", "event_id"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "chat_server_event_outbox",
            columns: table => new
            {
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                source_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_type = table.Column<string>(
                    type: "character varying(128)",
                    maxLength: 128,
                    nullable: false,
                    collation: "C"),
                event_version = table.Column<int>(type: "integer", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                partition_key = table.Column<string>(
                    type: "character varying(128)",
                    maxLength: 128,
                    nullable: false,
                    collation: "C"),
                payload = table.Column<byte[]>(type: "bytea", maxLength: 16384, nullable: false),
                attempt_count = table.Column<int>(type: "integer", nullable: false),
                next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                lease_owner = table.Column<string>(
                    type: "character varying(128)",
                    maxLength: 128,
                    nullable: true,
                    collation: "C"),
                lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                last_failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_chat_server_event_outbox", item => item.event_id);
                table.CheckConstraint("ck_chat_event_outbox_attempt_count", "attempt_count BETWEEN 0 AND 1000000");
                table.CheckConstraint(
                    "ck_chat_event_outbox_completion",
                    "published_at IS NULL OR (lease_owner IS NULL AND lease_expires_at IS NULL)");
                table.CheckConstraint(
                    "ck_chat_event_outbox_lease",
                    "(lease_owner IS NULL) = (lease_expires_at IS NULL)");
                table.CheckConstraint("ck_chat_event_outbox_payload_size", "octet_length(payload) BETWEEN 1 AND 16384");
                table.CheckConstraint("ck_chat_event_outbox_version", "event_version >= 1");
            });

        migrationBuilder.CreateIndex(
            name: "ux_chat_event_outbox_source",
            table: "chat_server_event_outbox",
            columns: SourceColumns,
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ix_chat_event_outbox_pending",
            table: "chat_server_event_outbox",
            columns: PendingColumns,
            filter: "published_at IS NULL");
        migrationBuilder.CreateIndex(
            name: "ix_chat_event_outbox_published",
            table: "chat_server_event_outbox",
            column: "published_at",
            filter: "published_at IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("chat_server_event_outbox");
}
