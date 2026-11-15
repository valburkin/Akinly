namespace Akinly;

/// <summary>Where an item went.</summary>
/// <param name="ItemId">The item.</param>
/// <param name="ClusterId">The cluster it is now in.</param>
/// <param name="Reason">Why it went there.</param>
public readonly record struct Placement(string ItemId, long ClusterId, PlacementReason Reason);
