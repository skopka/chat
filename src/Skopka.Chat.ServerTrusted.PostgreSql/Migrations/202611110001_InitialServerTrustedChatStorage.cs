using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skopka.Chat.ServerTrusted.PostgreSql.Migrations;

/// <summary>Initial server-trusted schema. The schema name is injected by the context's migrations assembly.</summary>
[DbContext(typeof(ServerTrustedChatDbContext))]
[Migration("202611110001_InitialServerTrustedChatStorage")]
public sealed class InitialServerTrustedChatStorage : Migration
{
    private readonly string _schema;

    /// <summary>Creates the migration for the default schema.</summary>
    public InitialServerTrustedChatStorage() : this(ServerTrustedChatSchema.Default)
    {
    }

    /// <summary>Creates the migration for a validated schema.</summary>
    public InitialServerTrustedChatStorage(string schema) => _schema = ServerTrustedChatSchema.Require(schema);

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var schema = _schema;
        migrationBuilder.EnsureSchema(schema);
        const string timestamp = "timestamp with time zone";

        migrationBuilder.CreateTable(
            name: "conversations",
            schema: schema,
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                host_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                kind = table.Column<short>(type: "smallint", nullable: false),
                status = table.Column<short>(type: "smallint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: timestamp, nullable: false),
                closed_at = table.Column<DateTimeOffset>(type: timestamp, nullable: true),
                last_activity_at = table.Column<DateTimeOffset>(type: timestamp, nullable: false),
                last_sequence = table.Column<long>(type: "bigint", nullable: false),
                change_revision = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_conversations", x => x.id);
                table.CheckConstraint("ck_conversations_kind", "kind IN (1, 2)");
                table.CheckConstraint("ck_conversations_status", "status IN (1, 2)");
                table.CheckConstraint("ck_conversations_closed", "(status = 2) = (closed_at IS NOT NULL)");
                table.CheckConstraint("ck_conversations_counters", "last_sequence >= 0 AND change_revision >= 0");
            });
        migrationBuilder.CreateIndex("ux_conversations_host_key", "conversations", "host_key", schema, unique: true);
        migrationBuilder.CreateIndex("ix_conversations_closed_at", "conversations", "closed_at", schema, filter: "status = 2");

        migrationBuilder.CreateTable(
            name: "participants",
            schema: schema,
            columns: table => new
            {
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<short>(type: "smallint", nullable: false),
                can_read = table.Column<bool>(type: "boolean", nullable: false),
                label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                joined_at = table.Column<DateTimeOffset>(type: timestamp, nullable: false),
                history_visible_from = table.Column<long>(type: "bigint", nullable: false),
                read_through = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_participants", x => new { x.conversation_id, x.user_id });
                table.CheckConstraint("ck_participants_role", "role IN (1, 2)");
                table.CheckConstraint("ck_participants_markers", "history_visible_from >= 0 AND read_through >= 0");
                table.ForeignKey("fk_participants_conversation", x => x.conversation_id, "conversations", "id", schema, onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("ix_participants_user_id", "participants", "user_id", schema);

        migrationBuilder.CreateTable(
            name: "messages",
            schema: schema,
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                sequence = table.Column<long>(type: "bigint", nullable: false),
                change_revision = table.Column<long>(type: "bigint", nullable: false),
                author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                author_label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                client_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                text = table.Column<string>(type: "text", nullable: true),
                retained_text = table.Column<string>(type: "text", nullable: true),
                reply_to_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                host_references = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: timestamp, nullable: false),
                edited_at = table.Column<DateTimeOffset>(type: timestamp, nullable: true),
                deleted_at = table.Column<DateTimeOffset>(type: timestamp, nullable: true),
                hidden_at = table.Column<DateTimeOffset>(type: timestamp, nullable: true),
                is_erased = table.Column<bool>(type: "boolean", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_messages", x => x.id);
                table.CheckConstraint("ck_messages_sequence", "sequence >= 1 AND change_revision >= 1");
                table.CheckConstraint("ck_messages_fingerprint", "octet_length(fingerprint) = 32");
                table.CheckConstraint("ck_messages_text_length", "text IS NULL OR char_length(text) <= 16000");
                table.CheckConstraint("ck_messages_erased", "NOT is_erased OR (text IS NULL AND retained_text IS NULL)");
                table.ForeignKey("fk_messages_conversation", x => x.conversation_id, "conversations", "id", schema, onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("ux_messages_conversation_sequence", "messages", ["conversation_id", "sequence"], schema, unique: true);
        migrationBuilder.CreateIndex("ux_messages_conversation_revision", "messages", ["conversation_id", "change_revision"], schema, unique: true);
        migrationBuilder.CreateIndex("ux_messages_idempotency", "messages", ["conversation_id", "author_user_id", "client_message_id"], schema, unique: true);
        migrationBuilder.CreateIndex("ix_messages_author_user_id", "messages", "author_user_id", schema);
        migrationBuilder.CreateIndex("ix_messages_unread", "messages", ["conversation_id", "sequence"], schema,
            filter: "deleted_at IS NULL AND hidden_at IS NULL AND NOT is_erased");

        migrationBuilder.CreateTable(
            name: "message_versions",
            schema: schema,
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                text = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_message_versions", x => new { x.message_id, x.version });
                table.ForeignKey("fk_message_versions_message", x => x.message_id, "messages", "id", schema, onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "attachments",
            schema: schema,
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                uploader_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                message_id = table.Column<Guid>(type: "uuid", nullable: true),
                position = table.Column<int>(type: "integer", nullable: false),
                file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                content_type = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false, collation: "C"),
                length = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: timestamp, nullable: false),
                is_ready = table.Column<bool>(type: "boolean", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_attachments", x => x.id);
                table.CheckConstraint("ck_attachments_length", "length > 0");
                table.CheckConstraint("ck_attachments_bound_ready", "message_id IS NULL OR is_ready");
                table.ForeignKey("fk_attachments_conversation", x => x.conversation_id, "conversations", "id", schema, onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("ix_attachments_message_id", "attachments", "message_id", schema);
        migrationBuilder.CreateIndex("ix_attachments_conversation_created", "attachments", ["conversation_id", "created_at"], schema);
        migrationBuilder.CreateIndex("ix_attachments_pending", "attachments", "created_at", schema, filter: "message_id IS NULL");

        migrationBuilder.CreateTable(
            name: "reports",
            schema: schema,
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                reporter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: timestamp, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_reports", x => x.id);
                table.ForeignKey("fk_reports_conversation", x => x.conversation_id, "conversations", "id", schema, onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("ux_reports_message_reporter", "reports", ["message_id", "reporter_user_id"], schema, unique: true);
        migrationBuilder.CreateIndex("ix_reports_conversation_id", "reports", "conversation_id", schema);

        migrationBuilder.CreateTable(
            name: "access_log",
            schema: schema,
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                action = table.Column<short>(type: "smallint", nullable: false),
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                report_id = table.Column<Guid>(type: "uuid", nullable: true),
                message_id = table.Column<Guid>(type: "uuid", nullable: true),
                from_sequence = table.Column<long>(type: "bigint", nullable: true),
                to_sequence = table.Column<long>(type: "bigint", nullable: true),
                at = table.Column<DateTimeOffset>(type: timestamp, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_access_log", x => x.id);
                table.CheckConstraint("ck_access_log_action", "action BETWEEN 1 AND 4");
            });
        migrationBuilder.CreateIndex("ix_access_log_conversation_at", "access_log", ["conversation_id", "at"], schema);
        migrationBuilder.CreateIndex("ix_access_log_actor_user_id", "access_log", "actor_user_id", schema);
        migrationBuilder.CreateIndex("ix_access_log_at", "access_log", "at", schema);

        migrationBuilder.CreateTable(
            name: "blob_deletions",
            schema: schema,
            columns: table => new
            {
                attachment_id = table.Column<Guid>(type: "uuid", nullable: false),
                queued_at = table.Column<DateTimeOffset>(type: timestamp, nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_blob_deletions", x => x.attachment_id));

        migrationBuilder.CreateTable(
            name: "event_outbox",
            schema: schema,
            columns: table => new
            {
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                event_version = table.Column<int>(type: "integer", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: timestamp, nullable: false),
                partition_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                payload = table.Column<byte[]>(type: "bytea", maxLength: 16384, nullable: false),
                attempt_count = table.Column<int>(type: "integer", nullable: false),
                next_attempt_at = table.Column<DateTimeOffset>(type: timestamp, nullable: false),
                lease_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, collation: "C"),
                lease_expires_at = table.Column<DateTimeOffset>(type: timestamp, nullable: true),
                last_failed_at = table.Column<DateTimeOffset>(type: timestamp, nullable: true),
                published_at = table.Column<DateTimeOffset>(type: timestamp, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_event_outbox", x => x.event_id);
                table.CheckConstraint("ck_event_outbox_payload_size", "octet_length(payload) BETWEEN 1 AND 16384");
                table.CheckConstraint("ck_event_outbox_version", "event_version >= 1");
                table.CheckConstraint("ck_event_outbox_attempt_count", "attempt_count BETWEEN 0 AND 1000000");
                table.CheckConstraint("ck_event_outbox_lease", "(lease_owner IS NULL) = (lease_expires_at IS NULL)");
                table.CheckConstraint("ck_event_outbox_completion", "published_at IS NULL OR (lease_owner IS NULL AND lease_expires_at IS NULL)");
            });
        migrationBuilder.CreateIndex("ix_event_outbox_due", "event_outbox", ["published_at", "next_attempt_at", "occurred_at", "event_id"], schema);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "event_outbox", "blob_deletions", "access_log", "reports", "attachments", "message_versions", "messages", "participants", "conversations" })
        {
            migrationBuilder.DropTable(table, _schema);
        }
    }
}
