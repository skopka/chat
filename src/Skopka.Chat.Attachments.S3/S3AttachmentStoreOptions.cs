namespace Skopka.Chat.Attachments.S3;

/// <summary>Names the S3-compatible bucket and isolated object-key prefix.</summary>
public sealed class S3AttachmentStoreOptions
{
    private string _bucketName = string.Empty;
    private string _keyPrefix = "skopka-chat/attachments/";

    /// <summary>Bucket containing encrypted attachment objects.</summary>
    public required string BucketName
    {
        get => _bucketName;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _bucketName = value;
        }
    }

    /// <summary>Prefix isolating chat attachment objects from other bucket data.</summary>
    public string KeyPrefix
    {
        get => _keyPrefix;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _keyPrefix = value.EndsWith('/') ? value : value + '/';
        }
    }
}
