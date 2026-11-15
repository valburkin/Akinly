using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Akinly.VectorData;

/// <summary>
/// Maps Akinly's ids to a connector's keys, deterministically. Names use only letters, digits,
/// <c>-</c> and <c>_</c>, which every database accepts in a key: an item's id, which can hold any
/// character, is written in base64url.
/// </summary>
internal abstract class Keys<TKey>
    where TKey : notnull
{
    /// <summary>Ids up to this many UTF-8 bytes are spelled out in their key; longer ones are hashed.</summary>
    public const int MaxItemIdBytes = 300;

    public TKey Cluster(long id) => From($"cluster-{id}");

    /// <summary>
    /// An item's key: its id in base64url, or for an id too long to be a key everywhere (Azure AI
    /// Search allows 1,024 characters, a PostgreSQL index about 2,700 bytes), its SHA-256.
    /// </summary>
    public TKey Item(string id)
    {
        var bytes = Encoding.UTF8.GetBytes(id);
        return bytes.Length <= MaxItemIdBytes
            ? From("item-" + Base64Url.EncodeToString(bytes))
            : From("itemh-" + Base64Url.EncodeToString(SHA256.HashData(bytes)));
    }

    public TKey Question(QuestionId id) => From($"question-{id.Value}");

    public TKey Check(long a, long b)
    {
        var (low, high) = a < b ? (a, b) : (b, a);
        return From($"check-{low}-{high}");
    }

    public TKey Meta(string name) => From($"meta-{name}");

    protected abstract TKey From(string name);
}

/// <summary>String keys: the name itself.</summary>
internal sealed class StringKeys : Keys<string>
{
    protected override string From(string name) => name;
}

/// <summary>
/// GUID keys: name-based UUIDs (RFC 9562, version 5) in a namespace of Akinly's own, so the same
/// id always maps to the same key.
/// </summary>
internal sealed class GuidKeys : Keys<Guid>
{
    private static readonly byte[] Namespace = new Guid("cd87723b-0912-4541-82d6-2a8df7df4089").ToByteArray(bigEndian: true);

    protected override Guid From(string name)
    {
        var bytes = new byte[Namespace.Length + Encoding.UTF8.GetByteCount(name)];
        Namespace.CopyTo(bytes, 0);
        Encoding.UTF8.GetBytes(name, bytes.AsSpan(Namespace.Length));

#pragma warning disable CA5350 // SHA-1 is what RFC 9562 specifies for version 5 UUIDs; it names records, it secures nothing.
        var hash = SHA1.HashData(bytes);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);   // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
