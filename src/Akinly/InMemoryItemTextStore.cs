using System.Collections.Concurrent;

namespace Akinly;

/// <summary>Items' texts kept in memory. Safe to use from several threads.</summary>
public sealed class InMemoryItemTextStore : IItemTextStore
{
    private readonly ConcurrentDictionary<string, string> _texts = new(StringComparer.Ordinal);

    /// <summary>How many texts it holds.</summary>
    public int Count => _texts.Count;

    /// <summary>Keeps an item's text, replacing any it had.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="text">Its text.</param>
    public void Set(string itemId, string text)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(text);
        _texts[itemId] = text;
    }

    /// <summary>Forgets an item's text.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>Whether it held one.</returns>
    public bool Remove(string itemId)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        return _texts.TryRemove(itemId, out _);
    }

    /// <inheritdoc />
    public ValueTask SetTextAsync(string itemId, string text, CancellationToken cancellationToken)
    {
        Set(itemId, text);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveTextAsync(string itemId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Remove(itemId));

    /// <inheritdoc />
    public ValueTask<string?> GetTextAsync(string itemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        return ValueTask.FromResult(_texts.TryGetValue(itemId, out var text) ? text : null);
    }
}
