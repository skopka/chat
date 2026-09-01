using Microsoft.EntityFrameworkCore;

namespace Skopka.Chat.Attachments.PostgreSql;

/// <summary>Isolated EF Core model for opaque encrypted attachment blobs.</summary>
public sealed class AttachmentDbContext : DbContext
{
    /// <summary>Creates a PostgreSQL attachment context.</summary>
    public AttachmentDbContext(DbContextOptions<AttachmentDbContext> options) : base(options)
    {
    }

    internal DbSet<EncryptedAttachmentEntity> Attachments => Set<EncryptedAttachmentEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<EncryptedAttachmentEntity>(entity =>
        {
            entity.ToTable("chat_attachments", table =>
            {
                table.HasCheckConstraint("ck_chat_attachments_ciphertext_length", "ciphertext_length > 0");
                table.HasCheckConstraint("ck_chat_attachments_ciphertext_size", "octet_length(ciphertext) = ciphertext_length");
                table.HasCheckConstraint("ck_chat_attachments_hash_size", "octet_length(ciphertext_sha256) = 32");
                table.HasCheckConstraint("ck_chat_attachments_expiry", "expires_at IS NULL OR expires_at > created_at");
            });
            entity.HasKey(item => item.AttachmentId).HasName("pk_chat_attachments");
            entity.Property(item => item.AttachmentId).HasColumnName("attachment_id").ValueGeneratedNever();
            entity.Property(item => item.ConversationId).HasColumnName("conversation_id");
            entity.Property(item => item.UploaderUserId).HasColumnName("uploader_user_id");
            entity.Property(item => item.CiphertextLength).HasColumnName("ciphertext_length");
            entity.Property(item => item.CiphertextSha256).HasColumnName("ciphertext_sha256").HasMaxLength(32);
            entity.Property(item => item.Ciphertext).HasColumnName("ciphertext");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.ExpiresAt).HasColumnName("expires_at");
            entity.HasIndex(item => item.ConversationId).HasDatabaseName("ix_chat_attachments_conversation_id");
            entity.HasIndex(item => item.ExpiresAt).HasDatabaseName("ix_chat_attachments_expires_at");
        });
    }
}

internal sealed class EncryptedAttachmentEntity
{
    public Guid AttachmentId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UploaderUserId { get; set; }
    public long CiphertextLength { get; set; }
    public byte[] CiphertextSha256 { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
