namespace Skopka.Chat.Attachments.PostgreSql;

/// <summary>Bounds PostgreSQL allocation for encrypted attachment blobs.</summary>
public sealed class PostgreSqlAttachmentStoreOptions
{
    /// <summary>Default maximum ciphertext stored in one <c>bytea</c> row (16 MiB).</summary>
    public const int DefaultMaxCiphertextBytes = 16 * 1024 * 1024;

    private int _maxCiphertextBytes = DefaultMaxCiphertextBytes;

    /// <summary>Maximum ciphertext allocation. Use S3-compatible storage for larger files.</summary>
    public int MaxCiphertextBytes
    {
        get => _maxCiphertextBytes;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "PostgreSQL attachment limit must be positive.");
            }

            _maxCiphertextBytes = value;
        }
    }
}
