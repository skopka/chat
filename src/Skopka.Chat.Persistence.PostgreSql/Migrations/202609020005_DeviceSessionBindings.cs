using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Skopka.Chat.Persistence.PostgreSql.Migrations;

/// <summary>Adds independent binding-v1 challenges and persistent device/session associations.</summary>
[DbContext(typeof(ChatDbContext))]
[Migration("202609020005_DeviceSessionBindings")]
public sealed class DeviceSessionBindings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE device_binding_challenges (
                challenge_id uuid CONSTRAINT pk_device_binding_challenges PRIMARY KEY,
                payload bytea NOT NULL,
                expires_at timestamptz NOT NULL,
                session_expires_at timestamptz NOT NULL,
                signature bytea NULL,
                bound_at timestamptz NULL,
                CONSTRAINT ck_binding_challenge_size CHECK (octet_length(payload) BETWEEN 1 AND 1024),
                CONSTRAINT ck_binding_challenge_signature CHECK (signature IS NULL OR octet_length(signature) = 64),
                CONSTRAINT ck_binding_challenge_consumption CHECK ((signature IS NULL) = (bound_at IS NULL)),
                CONSTRAINT ck_binding_challenge_expiry CHECK (expires_at <= session_expires_at)
            );
            CREATE INDEX ix_device_challenges_expiry ON device_binding_challenges (expires_at);
            CREATE INDEX ix_device_challenges_session_expiry ON device_binding_challenges (session_expires_at);
            CREATE TABLE device_session_bindings (
                service_id varchar(256) COLLATE "C" NOT NULL,
                user_id uuid NOT NULL,
                session_reference varchar(256) COLLATE "C" NOT NULL,
                device_id uuid NOT NULL,
                key_id uuid NOT NULL,
                bound_at timestamptz NOT NULL,
                expires_at timestamptz NOT NULL,
                CONSTRAINT pk_device_session_bindings PRIMARY KEY (service_id, user_id, session_reference),
                CONSTRAINT fk_device_bindings_device FOREIGN KEY (device_id) REFERENCES devices(device_id) ON DELETE RESTRICT,
                CONSTRAINT ck_binding_context_size CHECK (octet_length(service_id) BETWEEN 1 AND 256 AND octet_length(session_reference) BETWEEN 1 AND 256),
                CONSTRAINT ck_binding_session_expiry CHECK (expires_at > bound_at)
            );
            CREATE INDEX ix_device_bindings_device ON device_session_bindings(device_id);
            CREATE INDEX ix_device_bindings_expiry ON device_session_bindings(expires_at);
            """);
    }
    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("device_session_bindings");
        migrationBuilder.DropTable("device_binding_challenges");
    }
}
