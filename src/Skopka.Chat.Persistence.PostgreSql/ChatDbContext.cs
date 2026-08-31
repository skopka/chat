using Microsoft.EntityFrameworkCore;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Persistence.PostgreSql;

/// <summary>EF Core model containing public devices, conversations and encrypted envelopes only.</summary>
public sealed class ChatDbContext : DbContext
{
    /// <summary>Creates a PostgreSQL chat context.</summary>
    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options)
    {
    }

    internal DbSet<DeviceEntity> Devices => Set<DeviceEntity>();

    internal DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();

    internal DbSet<EnvelopeEntity> Envelopes => Set<EnvelopeEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DeviceEntity>(entity =>
        {
            entity.ToTable("devices", table =>
            {
                table.HasCheckConstraint("ck_devices_encryption_key_size", "octet_length(encryption_public_key) = 32");
                table.HasCheckConstraint("ck_devices_signing_key_size", "octet_length(signing_public_key) = 32");
                table.HasCheckConstraint("ck_devices_revocation_time", "revoked_at IS NULL OR revoked_at >= registered_at");
            });
            entity.HasKey(item => item.DeviceId).HasName("pk_devices");
            entity.Property(item => item.DeviceId).HasColumnName("device_id").ValueGeneratedNever();
            entity.Property(item => item.UserId).HasColumnName("user_id");
            entity.Property(item => item.KeyId).HasColumnName("key_id");
            entity.Property(item => item.EncryptionPublicKey).HasColumnName("encryption_public_key").HasMaxLength(ProtocolLimits.X25519PublicKeyBytes);
            entity.Property(item => item.SigningPublicKey).HasColumnName("signing_public_key").HasMaxLength(ProtocolLimits.Ed25519PublicKeyBytes);
            entity.Property(item => item.RegisteredAt).HasColumnName("registered_at");
            entity.Property(item => item.RevokedAt).HasColumnName("revoked_at");
            entity.HasIndex(item => item.UserId).HasDatabaseName("ix_devices_user_id");
            entity.HasIndex(item => new { item.DeviceId, item.KeyId }).IsUnique().HasDatabaseName("ux_devices_device_key");
        });

        modelBuilder.Entity<ConversationEntity>(entity =>
        {
            entity.ToTable("conversations", table =>
                table.HasCheckConstraint("ck_conversations_distinct_users", "first_user_id <> second_user_id"));
            entity.HasKey(item => item.ConversationId).HasName("pk_conversations");
            entity.Property(item => item.ConversationId).HasColumnName("conversation_id").ValueGeneratedNever();
            entity.Property(item => item.FirstUserId).HasColumnName("first_user_id");
            entity.Property(item => item.SecondUserId).HasColumnName("second_user_id");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(item => new { item.FirstUserId, item.SecondUserId }).HasDatabaseName("ix_conversations_users");
        });

        modelBuilder.Entity<EnvelopeEntity>(entity =>
        {
            entity.ToTable("envelopes", table =>
            {
                table.HasCheckConstraint("ck_envelopes_protocol_version", "protocol_version = 1");
                table.HasCheckConstraint("ck_envelopes_ephemeral_key_size", "octet_length(ephemeral_public_key) = 32");
                table.HasCheckConstraint("ck_envelopes_nonce_size", "octet_length(nonce) = 24");
                table.HasCheckConstraint("ck_envelopes_ciphertext_size", "octet_length(ciphertext) <= 65536");
                table.HasCheckConstraint("ck_envelopes_tag_size", "octet_length(authentication_tag) = 16");
                table.HasCheckConstraint("ck_envelopes_signature_size", "octet_length(signature) = 64");
                table.HasCheckConstraint("ck_envelopes_hash_size", "octet_length(canonical_hash) = 32");
                table.HasCheckConstraint("ck_envelopes_expiry", "expires_at IS NULL OR expires_at > sent_at");
            });
            entity.HasKey(item => item.MessageId).HasName("pk_envelopes");
            entity.Property(item => item.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            entity.Property(item => item.ProtocolVersion).HasColumnName("protocol_version");
            entity.Property(item => item.ConversationId).HasColumnName("conversation_id");
            entity.Property(item => item.SenderDeviceId).HasColumnName("sender_device_id");
            entity.Property(item => item.RecipientDeviceId).HasColumnName("recipient_device_id");
            entity.Property(item => item.SenderSigningKeyId).HasColumnName("sender_signing_key_id");
            entity.Property(item => item.RecipientEncryptionKeyId).HasColumnName("recipient_encryption_key_id");
            entity.Property(item => item.SentAt).HasColumnName("sent_at");
            entity.Property(item => item.ExpiresAt).HasColumnName("expires_at");
            entity.Property(item => item.EphemeralPublicKey).HasColumnName("ephemeral_public_key").HasMaxLength(ProtocolLimits.X25519PublicKeyBytes);
            entity.Property(item => item.Nonce).HasColumnName("nonce").HasMaxLength(ProtocolLimits.NonceBytes);
            entity.Property(item => item.Ciphertext).HasColumnName("ciphertext").HasMaxLength(ProtocolLimits.MaxCiphertextBytes);
            entity.Property(item => item.AuthenticationTag).HasColumnName("authentication_tag").HasMaxLength(ProtocolLimits.AuthenticationTagBytes);
            entity.Property(item => item.Signature).HasColumnName("signature").HasMaxLength(ProtocolLimits.SignatureBytes);
            entity.Property(item => item.CanonicalHash).HasColumnName("canonical_hash").HasMaxLength(32);
            entity.Property(item => item.AcceptedAt).HasColumnName("accepted_at");
            entity.Property(item => item.AcknowledgedAt).HasColumnName("acknowledged_at");
            entity.HasIndex(item => new
            {
                item.RecipientDeviceId,
                item.AcknowledgedAt,
                item.AcceptedAt,
                item.MessageId
            })
                .HasDatabaseName("ix_envelopes_pending_delivery");
            entity.HasIndex(item => item.ExpiresAt).HasDatabaseName("ix_envelopes_expires_at");
            entity.HasOne<ConversationEntity>().WithMany().HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_envelopes_conversations");
            entity.HasOne<DeviceEntity>().WithMany().HasForeignKey(item => item.SenderDeviceId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_envelopes_sender_device");
            entity.HasOne<DeviceEntity>().WithMany().HasForeignKey(item => item.RecipientDeviceId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_envelopes_recipient_device");
        });
    }
}
