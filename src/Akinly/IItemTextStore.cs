namespace Akinly;

/// <summary>
/// Where items' texts are kept, for a judge and a labeler to read: a text source you can also
/// write to. Implement it over your own database to keep texts out of memory, or to keep them
/// across restarts; <see cref="InMemoryItemTextStore"/> is the default.
/// </summary>
public interface IItemTextStore : IItemTextSource
{
    /// <summary>Keeps an item's text, replacing any it had.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="text">Its text.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    ValueTask SetTextAsync(string itemId, string text, CancellationToken cancellationToken);

    /// <summary>Forgets an item's text.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it held one.</returns>
    ValueTask<bool> RemoveTextAsync(string itemId, CancellationToken cancellationToken);
}
