using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Skopka.Chat.Persistence.PostgreSql.Migrations;

/// <summary>Adds the message-ID tie-breaker to the pending-delivery index.</summary>
[DbContext(typeof(ChatDbContext))]
[Migration("202609010002_DeterministicPendingDeliveryOrder")]
public sealed class DeterministicPendingDeliveryOrder : Migration
{
    private static readonly string[] DeterministicColumns =
        ["recipient_device_id", "acknowledged_at", "accepted_at", "message_id"];

    private static readonly string[] OriginalColumns =
        ["recipient_device_id", "acknowledged_at", "expires_at", "accepted_at"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("ix_envelopes_pending_delivery", "envelopes");
        migrationBuilder.CreateIndex(
            "ix_envelopes_pending_delivery",
            "envelopes",
            DeterministicColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("ix_envelopes_pending_delivery", "envelopes");
        migrationBuilder.CreateIndex(
            "ix_envelopes_pending_delivery",
            "envelopes",
            OriginalColumns);
    }
}
