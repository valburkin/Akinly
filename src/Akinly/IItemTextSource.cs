namespace Akinly;

/// <summary>
/// Where a judge reads items' texts. The clusterer itself never keeps text; a judge asks for it
/// only to put it in a prompt.
/// </summary>
public interface IItemTextSource
{
    /// <summary>An item's text.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The text, or null when it is not known (or no longer kept); then similarity alone decides for that item.</returns>
    ValueTask<string?> GetTextAsync(string itemId, CancellationToken cancellationToken);
}
