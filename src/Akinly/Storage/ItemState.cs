namespace Akinly.Storage;

/// <summary>Where an item is in the clustering.</summary>
public enum ItemState
{
    /// <summary>In a cluster.</summary>
    Placed = 0,

    /// <summary>Waiting for a judge's answer to its open question.</summary>
    Asking = 1,

    /// <summary>Waiting for room under <see cref="ClusteringOptions.MaxOpenQuestions"/> to ask its question.</summary>
    Waiting = 2,
}
