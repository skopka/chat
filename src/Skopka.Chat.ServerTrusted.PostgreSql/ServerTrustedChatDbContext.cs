using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.Extensions.DependencyInjection;
using Skopka.Chat.Server;

namespace Skopka.Chat.ServerTrusted.PostgreSql;

/// <summary>Validated PostgreSQL schema that holds every server-trusted table and the migrations history.</summary>
public static class ServerTrustedChatSchema
{
    /// <summary>Default schema name.</summary>
    public const string Default = "skopka_chat_trusted";

    /// <summary>Migrations-history table inside the schema.</summary>
    public const string MigrationsHistoryTable = "__skopka_chat_trusted_migrations";

    /// <summary>Whether a schema name matches <c>^[a-z_][a-z0-9_]{0,47}$</c> (also bounds the NOTIFY channel name).</summary>
    public static bool IsValid(string? schema) =>
        schema is { Length: > 0 and <= 48 } &&
        (char.IsAsciiLetterLower(schema[0]) || schema[0] == '_') &&
        schema.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    /// <summary>NOTIFY channel used by the live bridge for this schema.</summary>
    public static string ChannelFor(string schema) => schema + "_signals";

    internal static string Require(string? schema) =>
        IsValid(schema) ? schema! : throw new ArgumentException("The server-trusted schema name is invalid.", nameof(schema));
}

/// <summary>EF Core context of the server-trusted mode; every table lives in the configured schema.</summary>
public sealed class ServerTrustedChatDbContext : DbContext
{
    /// <summary>Creates a context; use <see cref="Configure"/> to build its options.</summary>
    public ServerTrustedChatDbContext(DbContextOptions<ServerTrustedChatDbContext> options) : base(options)
    {
        Schema = options.FindExtension<ServerTrustedSchemaExtension>()?.Schema ?? ServerTrustedChatSchema.Default;
    }

    /// <summary>Configured schema.</summary>
    public string Schema { get; }

    internal DbSet<ConversationRow> Conversations => Set<ConversationRow>();
    internal DbSet<ParticipantRow> Participants => Set<ParticipantRow>();
    internal DbSet<MessageRow> Messages => Set<MessageRow>();
    internal DbSet<MessageVersionRow> MessageVersions => Set<MessageVersionRow>();
    internal DbSet<AttachmentRow> Attachments => Set<AttachmentRow>();
    internal DbSet<ReportRow> Reports => Set<ReportRow>();
    internal DbSet<AccessLogRow> AccessLog => Set<AccessLogRow>();
    internal DbSet<BlobDeletionRow> BlobDeletions => Set<BlobDeletionRow>();
    internal DbSet<OutboxRow> Outbox => Set<OutboxRow>();

    /// <summary>
    /// Configures Npgsql with the schema-qualified model, schema-aware migrations and a migrations-history table inside
    /// the schema. Apply migrations from the host's migration step; nothing migrates at startup.
    /// </summary>
    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder builder,
        string connectionString,
        string schema = ServerTrustedChatSchema.Default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var validated = ServerTrustedChatSchema.Require(schema);
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(ServerTrustedChatSchema.MigrationsHistoryTable, validated));
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new ServerTrustedSchemaExtension(validated));
        builder.ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>();
        builder.ReplaceService<IMigrationsAssembly, SchemaMigrationsAssembly>();
        return builder;
    }

    /// <summary>Creates options for a context outside dependency injection (tests, migration tools).</summary>
    public static DbContextOptions<ServerTrustedChatDbContext> CreateOptions(string connectionString, string schema = ServerTrustedChatSchema.Default)
    {
        var builder = new DbContextOptionsBuilder<ServerTrustedChatDbContext>();
        Configure(builder, connectionString, schema);
        return builder.Options;
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ConversationRow>(entity =>
        {
            entity.ToTable("conversations");
            entity.HasKey(item => item.Id).HasName("pk_conversations");
            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(item => item.HostKey).HasColumnName("host_key").HasMaxLength(ServerTrustedChatLimits.MaxHostKeyLength).UseCollation("C");
            entity.Property(item => item.Kind).HasColumnName("kind");
            entity.Property(item => item.Status).HasColumnName("status");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.ClosedAt).HasColumnName("closed_at");
            entity.Property(item => item.LastActivityAt).HasColumnName("last_activity_at");
            entity.Property(item => item.LastSequence).HasColumnName("last_sequence");
            entity.Property(item => item.ChangeRevision).HasColumnName("change_revision");
            entity.HasIndex(item => item.HostKey).IsUnique().HasDatabaseName("ux_conversations_host_key");
        });

        modelBuilder.Entity<ParticipantRow>(entity =>
        {
            entity.ToTable("participants");
            entity.HasKey(item => new { item.ConversationId, item.UserId }).HasName("pk_participants");
            entity.Property(item => item.ConversationId).HasColumnName("conversation_id");
            entity.Property(item => item.UserId).HasColumnName("user_id");
            entity.Property(item => item.Role).HasColumnName("role");
            entity.Property(item => item.CanRead).HasColumnName("can_read");
            entity.Property(item => item.Label).HasColumnName("label").HasMaxLength(ServerTrustedChatLimits.MaxLabelLength).UseCollation("C");
            entity.Property(item => item.JoinedAt).HasColumnName("joined_at");
            entity.Property(item => item.HistoryVisibleFrom).HasColumnName("history_visible_from");
            entity.Property(item => item.ReadThrough).HasColumnName("read_through");
            entity.HasIndex(item => item.UserId).HasDatabaseName("ix_participants_user_id");
            entity.HasOne<ConversationRow>().WithMany().HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_participants_conversation");
        });

        modelBuilder.Entity<MessageRow>(entity =>
        {
            entity.ToTable("messages");
            entity.HasKey(item => item.Id).HasName("pk_messages");
            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(item => item.ConversationId).HasColumnName("conversation_id");
            entity.Property(item => item.Sequence).HasColumnName("sequence");
            entity.Property(item => item.ChangeRevision).HasColumnName("change_revision");
            entity.Property(item => item.AuthorUserId).HasColumnName("author_user_id");
            entity.Property(item => item.AuthorLabel).HasColumnName("author_label").HasMaxLength(ServerTrustedChatLimits.MaxLabelLength).UseCollation("C");
            entity.Property(item => item.ClientMessageId).HasColumnName("client_message_id");
            entity.Property(item => item.Fingerprint).HasColumnName("fingerprint").HasMaxLength(32);
            entity.Property(item => item.Text).HasColumnName("text");
            entity.Property(item => item.RetainedText).HasColumnName("retained_text");
            entity.Property(item => item.ReplyToMessageId).HasColumnName("reply_to_message_id");
            entity.Property(item => item.ReferencesJson).HasColumnName("host_references");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.EditedAt).HasColumnName("edited_at");
            entity.Property(item => item.DeletedAt).HasColumnName("deleted_at");
            entity.Property(item => item.HiddenAt).HasColumnName("hidden_at");
            entity.Property(item => item.IsErased).HasColumnName("is_erased");
            entity.HasIndex(item => new { item.ConversationId, item.Sequence }).IsUnique().HasDatabaseName("ux_messages_conversation_sequence");
            entity.HasIndex(item => new { item.ConversationId, item.ChangeRevision }).IsUnique().HasDatabaseName("ux_messages_conversation_revision");
            entity.HasIndex(item => new { item.ConversationId, item.AuthorUserId, item.ClientMessageId }).IsUnique().HasDatabaseName("ux_messages_idempotency");
            entity.HasIndex(item => item.AuthorUserId).HasDatabaseName("ix_messages_author_user_id");
            entity.HasOne<ConversationRow>().WithMany().HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_messages_conversation");
        });

        modelBuilder.Entity<MessageVersionRow>(entity =>
        {
            entity.ToTable("message_versions");
            entity.HasKey(item => new { item.MessageId, item.Version }).HasName("pk_message_versions");
            entity.Property(item => item.MessageId).HasColumnName("message_id");
            entity.Property(item => item.Version).HasColumnName("version");
            entity.Property(item => item.Text).HasColumnName("text");
            entity.HasOne<MessageRow>().WithMany().HasForeignKey(item => item.MessageId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_message_versions_message");
        });

        modelBuilder.Entity<AttachmentRow>(entity =>
        {
            entity.ToTable("attachments");
            entity.HasKey(item => item.Id).HasName("pk_attachments");
            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(item => item.ConversationId).HasColumnName("conversation_id");
            entity.Property(item => item.UploaderUserId).HasColumnName("uploader_user_id");
            entity.Property(item => item.MessageId).HasColumnName("message_id");
            entity.Property(item => item.Position).HasColumnName("position");
            entity.Property(item => item.FileName).HasColumnName("file_name").HasMaxLength(ServerTrustedChatLimits.MaxFileNameLength);
            entity.Property(item => item.ContentType).HasColumnName("content_type").HasMaxLength(ServerTrustedChatLimits.MaxContentTypeLength).UseCollation("C");
            entity.Property(item => item.Length).HasColumnName("length");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.IsReady).HasColumnName("is_ready");
            entity.HasIndex(item => item.MessageId).HasDatabaseName("ix_attachments_message_id");
            entity.HasIndex(item => new { item.ConversationId, item.CreatedAt }).HasDatabaseName("ix_attachments_conversation_created");
            entity.HasOne<ConversationRow>().WithMany().HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_attachments_conversation");
        });

        modelBuilder.Entity<ReportRow>(entity =>
        {
            entity.ToTable("reports");
            entity.HasKey(item => item.Id).HasName("pk_reports");
            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(item => item.ConversationId).HasColumnName("conversation_id");
            entity.Property(item => item.MessageId).HasColumnName("message_id");
            entity.Property(item => item.ReporterUserId).HasColumnName("reporter_user_id");
            entity.Property(item => item.ReasonCode).HasColumnName("reason_code").HasMaxLength(ServerTrustedChatLimits.MaxReasonCodeLength).UseCollation("C");
            entity.Property(item => item.Comment).HasColumnName("comment").HasMaxLength(ServerTrustedChatLimits.MaxReportCommentLength);
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(item => new { item.MessageId, item.ReporterUserId }).IsUnique().HasDatabaseName("ux_reports_message_reporter");
            entity.HasIndex(item => item.ConversationId).HasDatabaseName("ix_reports_conversation_id");
            entity.HasOne<ConversationRow>().WithMany().HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_reports_conversation");
        });

        modelBuilder.Entity<AccessLogRow>(entity =>
        {
            entity.ToTable("access_log");
            entity.HasKey(item => item.Id).HasName("pk_access_log");
            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(item => item.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(item => item.Action).HasColumnName("action");
            entity.Property(item => item.ConversationId).HasColumnName("conversation_id");
            entity.Property(item => item.ReportId).HasColumnName("report_id");
            entity.Property(item => item.MessageId).HasColumnName("message_id");
            entity.Property(item => item.FromSequence).HasColumnName("from_sequence");
            entity.Property(item => item.ToSequence).HasColumnName("to_sequence");
            entity.Property(item => item.At).HasColumnName("at");
            entity.HasIndex(item => new { item.ConversationId, item.At }).HasDatabaseName("ix_access_log_conversation_at");
            entity.HasIndex(item => item.ActorUserId).HasDatabaseName("ix_access_log_actor_user_id");
            entity.HasIndex(item => item.At).HasDatabaseName("ix_access_log_at");
        });

        modelBuilder.Entity<BlobDeletionRow>(entity =>
        {
            entity.ToTable("blob_deletions");
            entity.HasKey(item => item.AttachmentId).HasName("pk_blob_deletions");
            entity.Property(item => item.AttachmentId).HasColumnName("attachment_id").ValueGeneratedNever();
            entity.Property(item => item.QueuedAt).HasColumnName("queued_at");
        });

        modelBuilder.Entity<OutboxRow>(entity =>
        {
            entity.ToTable("event_outbox");
            entity.HasKey(item => item.EventId).HasName("pk_event_outbox");
            entity.Property(item => item.EventId).HasColumnName("event_id").ValueGeneratedNever();
            entity.Property(item => item.EventType).HasColumnName("event_type").HasMaxLength(ChatServerEventTypes.MaxIdentifierLength).UseCollation("C");
            entity.Property(item => item.EventVersion).HasColumnName("event_version");
            entity.Property(item => item.OccurredAt).HasColumnName("occurred_at");
            entity.Property(item => item.PartitionKey).HasColumnName("partition_key").HasMaxLength(ChatServerEventTypes.MaxIdentifierLength).UseCollation("C");
            entity.Property(item => item.Payload).HasColumnName("payload").HasMaxLength(ChatServerEventTypes.MaxPayloadBytes);
            entity.Property(item => item.AttemptCount).HasColumnName("attempt_count");
            entity.Property(item => item.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(item => item.LeaseOwner).HasColumnName("lease_owner").HasMaxLength(ChatServerEventTypes.MaxIdentifierLength).UseCollation("C");
            entity.Property(item => item.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(item => item.LastFailedAt).HasColumnName("last_failed_at");
            entity.Property(item => item.PublishedAt).HasColumnName("published_at");
            entity.HasIndex(item => new { item.PublishedAt, item.NextAttemptAt, item.OccurredAt, item.EventId }).HasDatabaseName("ix_event_outbox_due");
        });
    }
}

internal sealed class ServerTrustedSchemaExtension(string schema) : IDbContextOptionsExtension
{
    private ExtensionInfo? _info;

    public string Schema { get; } = schema;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(ServerTrustedSchemaExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => string.Empty;

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["SkopkaChatServerTrusted:Schema"] = ((ServerTrustedSchemaExtension)Extension).Schema;
    }
}

internal sealed class SchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), (context as ServerTrustedChatDbContext)?.Schema, designTime);
}

#pragma warning disable EF1001 // MigrationsAssembly is the supported extension point for constructor-injected migrations.
internal sealed class SchemaMigrationsAssembly(
    ICurrentDbContext currentContext,
    IDbContextOptions options,
    IMigrationsIdGenerator idGenerator,
    IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
    : MigrationsAssembly(currentContext, options, idGenerator, logger)
{
    private readonly string _schema = (currentContext.Context as ServerTrustedChatDbContext)?.Schema ?? ServerTrustedChatSchema.Default;

    public override Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
    {
        ArgumentNullException.ThrowIfNull(migrationClass);
        if (migrationClass.GetConstructor([typeof(string)]) is { } constructor)
        {
            var migration = (Migration)constructor.Invoke([_schema]);
            migration.ActiveProvider = activeProvider;
            return migration;
        }

        return base.CreateMigration(migrationClass, activeProvider);
    }
}
#pragma warning restore EF1001
