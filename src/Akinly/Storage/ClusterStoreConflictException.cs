namespace Akinly.Storage;

/// <summary>
/// Another writer changed the store since it was read: the change set was not applied. The
/// clusterer reads again and recomputes.
/// </summary>
public sealed class ClusterStoreConflictException : Exception
{
    /// <summary>A conflict with no details.</summary>
    public ClusterStoreConflictException()
        : base("The cluster store changed since it was read.")
    {
    }

    /// <summary>A conflict with a message.</summary>
    /// <param name="message">What happened.</param>
    public ClusterStoreConflictException(string message)
        : base(message)
    {
    }

    /// <summary>A conflict caused by another exception.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The cause.</param>
    public ClusterStoreConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>A conflict between the version the writer read and the store's version now.</summary>
    /// <param name="expectedVersion">The version the writer read.</param>
    /// <param name="actualVersion">The store's version now.</param>
    public ClusterStoreConflictException(long expectedVersion, long actualVersion)
        : base($"The cluster store is at version {actualVersion}, not {expectedVersion}: another writer changed it.")
    {
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    /// <summary>The version the writer read, when known.</summary>
    public long? ExpectedVersion { get; }

    /// <summary>The store's version when the write was refused, when known.</summary>
    public long? ActualVersion { get; }
}
