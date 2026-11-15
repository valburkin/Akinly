namespace Akinly;

/// <summary>Why an item went where it did.</summary>
public enum PlacementReason
{
    /// <summary>Its similarity to the cluster reached the join threshold.</summary>
    Similarity = 0,

    /// <summary>A judge said it belongs there.</summary>
    Judge = 1,

    /// <summary>No cluster took it, so it started one.</summary>
    NewCluster = 2,
}
