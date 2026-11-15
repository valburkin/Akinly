using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Akinly.Storage;
using Akinly.VectorData.Records;
using Microsoft.Extensions.VectorData;

namespace Akinly.VectorData;

/// <summary>The store over a vector database, for one key type. See <see cref="VectorDataClusterStore"/>.</summary>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SemaphoreSlim only serializes writes; it holds no unmanaged resource unless its AvailableWaitHandle is used, which it never is.")]
internal sealed class VectorDataStore<TKey> : IClusterStore, IWriteSteps
    where TKey : notnull
{
    /// <summary>The format of the records this version writes; a newer one is refused.</summary>
    public const int SchemaVersion = 1;

    private const string InfoName = "info";
    private const string JournalName = "journal";

    /// <summary>
    /// The journal is split into parts of this many characters, written and read a few at a time,
    /// so a large change set stays within every database's message limits.
    /// </summary>
    private const int JournalPartLength = 512 * 1024;
    private const int JournalPartsPerCall = 4;

    /// <summary>The largest journal, in characters: well within what one .NET string can hold.</summary>
    private const double MaxJournalCharacters = 400_000_000;

    /// <summary>
    /// The most bytes of vectors one page carries, well under Qdrant's 4 MB message limit: pages
    /// of records with vectors hold fewer than <see cref="VectorDataClusterStoreOptions.PageSize"/>
    /// when the vectors are long.
    /// </summary>
    private const int PageBytes = 2 * 1024 * 1024;
    private const int PlacedState = (int)ItemState.Placed;
    private const int WaitingState = (int)ItemState.Waiting;

    private readonly VectorDataClusterStoreOptions _options;
    private readonly Keys<TKey> _keys;
    private readonly VectorStoreCollection<TKey, ClusterRecord<TKey>> _clusters;
    private readonly VectorStoreCollection<TKey, ItemRecord<TKey>> _items;
    private readonly VectorStoreCollection<TKey, QuestionRecord<TKey>> _questions;
    private readonly VectorStoreCollection<TKey, CheckRecord<TKey>> _checks;
    private readonly VectorStoreCollection<TKey, MetaRecord<TKey>> _meta;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _clusterPage;
    private readonly int _itemPage;
    private volatile bool _collectionsReady;

    public VectorDataStore(VectorStore store, VectorDataClusterStoreOptions options, Keys<TKey> keys)
    {
        _options = options;
        _keys = keys;

        // A cluster carries its centroid (4 bytes a dimension) and its sum (8, about 11 in
        // base64); an item its embedding.
        _clusterPage = Math.Clamp(PageBytes / ((options.Dimensions * 16) + 512), 1, options.PageSize);
        _itemPage = Math.Clamp(PageBytes / ((options.Dimensions * 4) + 1024), 1, options.PageSize);
        _clusters = store.GetCollection<TKey, ClusterRecord<TKey>>(options.CollectionName("clusters"), ClusterDefinition(options));
        _items = store.GetCollection<TKey, ItemRecord<TKey>>(options.CollectionName("items"), ItemDefinition(options));
        _questions = store.GetCollection<TKey, QuestionRecord<TKey>>(options.CollectionName("questions"), QuestionDefinition(options));
        _checks = store.GetCollection<TKey, CheckRecord<TKey>>(options.CollectionName("checks"), BodyDefinition(options));
        _meta = store.GetCollection<TKey, MetaRecord<TKey>>(options.CollectionName("meta"), MetaDefinition(options));
    }

    /// <inheritdoc />
    public Func<string, Task>? OnStep { get; set; }

    public async Task<StoreInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        return await ReadInfoAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClusterNeighbor>> NearestClustersAsync(ReadOnlyMemory<float> vector, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        if (count == 0 || vector.Length != _options.Dimensions)
        {
            return [];
        }

        // A large search (a big batch asks for its own new clusters too) goes a page at a time,
        // so no answer passes the database's message limit.
        var wanted = Widen(count);
        var found = new Dictionary<long, ClusterNeighbor>();
        for (var skip = 0; skip < wanted; skip += _clusterPage)
        {
            var take = Math.Min(_clusterPage, wanted - skip);
            var options = new VectorSearchOptions<ClusterRecord<TKey>> { Filter = r => r.Live, IncludeVectors = true, Skip = skip };
            var page = 0;
            await foreach (var result in _clusters.SearchAsync(vector, take, options, cancellationToken).ConfigureAwait(false))
            {
                var cluster = ToStored(result.Record);
                found.TryAdd(cluster.Id, new ClusterNeighbor(cluster, TensorPrimitives.Dot(vector.Span, cluster.Centroid.Span)));
                page++;
            }

            if (page < take)
            {
                break;
            }
        }

        // Similarities are recomputed from the vectors, so the order is exact and ties go by id,
        // whatever the index's distance function or rounding.
        return [.. found.Values.OrderByDescending(n => n.Similarity).ThenBy(n => n.Cluster.Id).Take(count)];
    }

    public async Task<IReadOnlyList<StoredCluster>> GetClustersAsync(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        var wanted = ids.Distinct().ToList();
        var found = new Dictionary<long, StoredCluster>();
        foreach (var page in wanted.Chunk(_clusterPage))
        {
            await foreach (var record in _clusters.GetAsync(page.Select(_keys.Cluster), new RecordRetrievalOptions { IncludeVectors = true }, cancellationToken).ConfigureAwait(false))
            {
                found[record.ClusterId] = ToStored(record);
            }
        }

        return [.. wanted.Where(found.ContainsKey).Select(id => found[id])];
    }

    public async IAsyncEnumerable<StoredCluster> ListClustersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var record in PageAsync(_clusters, _clusterPage, last => r => r.Live && r.ClusterId > last, r => r.ClusterId, r => r.ClusterId, cancellationToken).ConfigureAwait(false))
        {
            yield return ToStored(record);
        }
    }

    public async Task<IReadOnlyList<StoredCluster>> GetChangedClustersAsync(long afterId, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        var changed = new List<StoredCluster>();
        await foreach (var record in PageAsync(_clusters, _clusterPage, last => r => r.Live && r.Changed && r.ClusterId > last, r => r.ClusterId, r => r.ClusterId, cancellationToken, afterId, count).ConfigureAwait(false))
        {
            changed.Add(ToStored(record));
        }

        return changed;
    }

    public async Task<IReadOnlyList<StoredItem>> GetItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        var wanted = ids.Distinct(StringComparer.Ordinal).ToList();
        var found = new Dictionary<string, StoredItem>(StringComparer.Ordinal);
        foreach (var page in wanted.Chunk(_itemPage))
        {
            await foreach (var record in _items.GetAsync(page.Select(_keys.Item), new RecordRetrievalOptions { IncludeVectors = true }, cancellationToken).ConfigureAwait(false))
            {
                found[record.ItemId] = ToStored(record);
            }
        }

        return [.. wanted.Where(found.ContainsKey).Select(id => found[id])];
    }

    public async Task<IReadOnlyList<StoredItem>> GetMembersAsync(long clusterId, CancellationToken cancellationToken)
    {
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        var members = new List<StoredItem>();
        await foreach (var record in PageAsync(_items, _itemPage, last => r => r.ClusterId == clusterId && r.State == PlacedState && r.Sequence > last, r => r.Sequence, r => r.Sequence, cancellationToken).ConfigureAwait(false))
        {
            members.Add(ToStored(record));
        }

        return members;
    }

    public async Task<IReadOnlyList<string>> GetExamplesAsync(long clusterId, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var clusters = await GetClustersAsync([clusterId], cancellationToken).ConfigureAwait(false);
        if (count == 0 || clusters.Count == 0 || clusters[0].Count == 0)
        {
            return [];
        }

        var centroid = clusters[0].Centroid;
        // At least one more than asked for, to see whether the last one taken is tied with the next.
        var wanted = Math.Max(Widen(count), count == int.MaxValue ? count : count + 1);
        var options = new VectorSearchOptions<ItemRecord<TKey>> { Filter = r => r.ClusterId == clusterId && r.State == PlacedState, IncludeVectors = true };
        var found = new List<(string Id, long Sequence, double Similarity)>();
        await foreach (var result in _items.SearchAsync(centroid, wanted, options, cancellationToken).ConfigureAwait(false))
        {
            found.Add((result.Record.ItemId, result.Record.Sequence, TensorPrimitives.Dot(centroid.Span, result.Record.Embedding.Span)));
        }

        // Only all the members give the exact answer when the search came back with fewer than
        // the cluster could fill (an approximate index that filters after its scan), or cut
        // through a run of tied members (duplicate texts): then ties go by sequence.
        var ordered = found.OrderByDescending(f => f.Similarity).ThenBy(f => f.Sequence).ToList();
        var shortOfMembers = found.Count < Math.Min(wanted, clusters[0].Count);
        var cutTie = found.Count >= wanted && found.Count > count && ordered[count - 1].Similarity <= ordered[^1].Similarity;
        if (shortOfMembers || cutTie)
        {
            var members = await GetMembersAsync(clusterId, cancellationToken).ConfigureAwait(false);
            ordered = [.. members.Select(m => (m.Id, m.Sequence, (double)TensorPrimitives.Dot(centroid.Span, m.Embedding.Span)))
                .OrderByDescending(f => f.Item3).ThenBy(f => f.Sequence)];
        }

        return [.. ordered.Take(count).Select(f => f.Id)];
    }

    public async Task<IReadOnlyList<StoredItem>> GetWaitingItemsAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        var waiting = new List<StoredItem>();
        await foreach (var record in PageAsync(_items, _itemPage, last => r => r.State == WaitingState && r.Sequence > last, r => r.Sequence, r => r.Sequence, cancellationToken, 0, count).ConfigureAwait(false))
        {
            waiting.Add(ToStored(record));
        }

        return waiting;
    }

    public async Task<IReadOnlyList<StoredQuestion>> GetQuestionsAsync(IEnumerable<QuestionId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        var wanted = ids.Distinct().ToList();
        var found = new Dictionary<QuestionId, StoredQuestion>();
        foreach (var page in wanted.Chunk(_options.PageSize))
        {
            await foreach (var record in _questions.GetAsync(page.Select(_keys.Question), cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                var question = Read(record.Body, StoreJson.Default.StoredQuestion);
                found[question.Question.Id] = question;
            }
        }

        return [.. wanted.Where(found.ContainsKey).Select(id => found[id])];
    }

    public async Task<IReadOnlyList<StoredQuestion>> GetOpenQuestionsAsync(CancellationToken cancellationToken)
    {
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        var open = new List<StoredQuestion>();
        await foreach (var record in PageAsync(_questions, _options.PageSize, last => r => r.Open && r.QuestionId > last, r => r.QuestionId, r => r.QuestionId, cancellationToken, includeVectors: false).ConfigureAwait(false))
        {
            open.Add(Read(record.Body, StoreJson.Default.StoredQuestion));
        }

        return open;
    }

    public async Task<IReadOnlyList<MergeCheck>> GetMergeChecksAsync(IEnumerable<(long ClusterId, long OtherClusterId)> pairs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        var wanted = pairs.Select(p => MergeCheck.Key(p.ClusterId, p.OtherClusterId)).Distinct().ToList();
        var found = new Dictionary<(long, long), MergeCheck>();
        foreach (var page in wanted.Chunk(_options.PageSize))
        {
            await foreach (var record in _checks.GetAsync(page.Select(p => _keys.Check(p.Item1, p.Item2)), cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                var check = Read(record.Body, StoreJson.Default.MergeCheck);
                found[MergeCheck.Key(check.ClusterId, check.OtherClusterId)] = check;
            }
        }

        return [.. wanted.Where(found.ContainsKey).Select(key => found[key])];
    }

    public async Task ApplyAsync(ChangeSet changes, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Info.Version != expectedVersion + 1)
        {
            throw new ArgumentException("A change set moves the store's version up by exactly one.", nameof(changes));
        }

        Check(changes);
        await CollectionsAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Finish what an earlier writer left half-done first, so this change set is never
            // computed against, or written over, a half-applied one.
            await RecoverAsync(cancellationToken).ConfigureAwait(false);

            // One writer at a time: the version check catches another writer that came first,
            // but cannot make the write atomic with it (vector databases offer no compare-and-swap).
            var info = await ReadInfoAsync(cancellationToken).ConfigureAwait(false);
            if (info.Version != expectedVersion)
            {
                throw new ClusterStoreConflictException(expectedVersion, info.Version);
            }

            // A change set of metadata only is one record: written as it is, no journal needed.
            if (changes.IsEmpty)
            {
                await WriteInfoAsync(changes.Info, cancellationToken).ConfigureAwait(false);
                return;
            }

            // The journal first: if the write stops in the middle, the next write (or the next
            // process's first write) finishes it before anything else. From here on the write is
            // not canceled: stopping it would only leave work for the next one.
            var parts = await WriteJournalAsync(changes).ConfigureAwait(false);
            await StepAsync("journal").ConfigureAwait(false);
            await WriteAsync(changes, CancellationToken.None).ConfigureAwait(false);
            await DeleteJournalAsync(parts, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Refuses a change set the database could not store, before anything is written: once in the
    /// journal, a change set that cannot be written would stop every later write.
    /// </summary>
    private void Check(ChangeSet changes)
    {
        if (changes.Info.Dimensions != 0 && changes.Info.Dimensions != _options.Dimensions)
        {
            throw new EmbeddingModelMismatchException(
                $"The embeddings have {changes.Info.Dimensions} dimensions, but the store's collections were made for {_options.Dimensions}.");
        }

        if (changes.Items.Any(i => i.Embedding.Length != _options.Dimensions) || changes.Clusters.Any(c => c.Centroid.Length != _options.Dimensions))
        {
            throw new EmbeddingModelMismatchException($"A vector in the change set does not have the store's {_options.Dimensions} dimensions.");
        }

        if (!changes.Items.Select(i => i.Id).Concat(changes.RemovedItemIds).All(Storable))
        {
            throw new ArgumentException("An item id holds a NUL character or an unpaired surrogate, which databases cannot store.", nameof(changes));
        }

        if (!changes.Clusters.All(c => c.Label is null || Storable(c.Label)))
        {
            throw new ArgumentException("A cluster label holds a NUL character or an unpaired surrogate, which databases cannot store.", nameof(changes));
        }

        if (Repeats(changes.Items.Select(i => i.Id), StringComparer.Ordinal) || Repeats(changes.RemovedItemIds, StringComparer.Ordinal)
            || Repeats(changes.Clusters.Select(c => c.Id)) || Repeats(changes.Questions.Select(q => q.Question.Id))
            || Repeats(changes.MergeChecks.Select(c => MergeCheck.Key(c.ClusterId, c.OtherClusterId))))
        {
            throw new ArgumentException("A change set holds the same record twice.", nameof(changes));
        }

        // The journal holds the whole change set as one text before it is cut into parts: base64
        // takes about 5.4 characters a float, 10.7 a double.
        var characters = ((double)changes.Items.Count * _options.Dimensions * 5.4) + ((double)changes.Clusters.Count * _options.Dimensions * 16.1);
        if (characters > MaxJournalCharacters)
        {
            throw new ArgumentException(
                $"The change set is too large to journal ({changes.Items.Count:N0} items and {changes.Clusters.Count:N0} clusters of {_options.Dimensions:N0} dimensions): add fewer items per call.",
                nameof(changes));
        }
    }

    /// <summary>Text every database can store: no NUL, and valid UTF-16 (every surrogate paired).</summary>
    private static bool Storable(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0' || char.IsLowSurrogate(c) || (char.IsHighSurrogate(c) && (++i >= text.Length || !char.IsLowSurrogate(text[i]))))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Repeats<T>(IEnumerable<T> values, IEqualityComparer<T>? comparer = null)
    {
        var seen = new HashSet<T>(comparer);
        return !values.All(seen.Add);
    }

    /// <summary>Makes sure the collections exist.</summary>
    private async Task CollectionsAsync(CancellationToken cancellationToken)
    {
        if (_collectionsReady)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_collectionsReady)
            {
                await _clusters.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
                await _items.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
                await _questions.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
                await _checks.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
                await _meta.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
                _collectionsReady = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Finishes a change set an earlier write left in the journal, this process's or another's:
    /// every write looks, at the cost of one read. Only writers do this, under the gate, so a
    /// process that only reads never races a writer's journal.
    /// </summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        // The store's schema first, so a newer Akinly's store is refused before its journal is read.
        var info = await ReadInfoAsync(cancellationToken).ConfigureAwait(false);
        if (await _meta.GetAsync(_keys.Meta(JournalName), cancellationToken: cancellationToken).ConfigureAwait(false) is { } journal)
        {
            var header = Read(journal.Body, StoreJson.Default.JournalHeader);
            if (header.SchemaVersion > SchemaVersion)
            {
                throw new InvalidDataException($"The store's journal was written by a newer Akinly (schema {header.SchemaVersion}); this version reads up to {SchemaVersion}.");
            }

            var pending = await ReadJournalAsync(header, cancellationToken).ConfigureAwait(false);
            if (pending.Info.Version > info.Version)
            {
                try
                {
                    await WriteAsync(pending, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"The store's journal holds change set {pending.Info.Version}, which could not be applied: {exception.GetType().Name}. "
                        + "Fix the cause and retry; until then every write stops here, so nothing is computed from half-written data.",
                        exception);
                }
            }

            await DeleteJournalAsync(header.Parts, cancellationToken).ConfigureAwait(false);
        }

    }

    /// <summary>Writes a change set to the journal: its parts, then the header that makes them count.</summary>
    /// <returns>The number of parts.</returns>
    private async Task<int> WriteJournalAsync(ChangeSet changes)
    {
        // The default encoder escapes everything outside ASCII, so the JSON can be cut anywhere
        // without splitting a character.
        var json = JsonSerializer.Serialize(changes, StoreJson.Default.ChangeSet);
        var parts = json.Chunk(JournalPartLength).Select((part, i) => Meta(JournalPart(i), new string(part))).ToList();
        foreach (var call in parts.Chunk(JournalPartsPerCall))
        {
            await _meta.UpsertAsync(call, CancellationToken.None).ConfigureAwait(false);
        }

        await StepAsync("parts").ConfigureAwait(false);
        var header = new JournalHeader(changes.Info.Version, parts.Count, SchemaVersion);
        await _meta.UpsertAsync(Meta(JournalName, JsonSerializer.Serialize(header, StoreJson.Default.JournalHeader)), CancellationToken.None).ConfigureAwait(false);
        return parts.Count;
    }

    private async Task<ChangeSet> ReadJournalAsync(JournalHeader header, CancellationToken cancellationToken)
    {
        var json = new System.Text.StringBuilder();
        foreach (var call in Enumerable.Range(0, header.Parts).Chunk(JournalPartsPerCall))
        {
            var found = new Dictionary<int, string>();
            var keys = call.Select(i => _keys.Meta(JournalPart(i))).ToList();
            await foreach (var record in _meta.GetAsync(keys, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                found[keys.IndexOf(record.Key)] = record.Body;
            }

            for (var i = 0; i < call.Length; i++)
            {
                json.Append(found.TryGetValue(i, out var part) ? part : throw new InvalidDataException($"Part {call[i]} of the store's journal is missing."));
            }
        }

        var pending = Read(json.ToString(), StoreJson.Default.ChangeSet);
        return pending.Info.Version == header.Version
            ? pending
            : throw new InvalidDataException("The store's journal parts belong to another change set than its header.");
    }

    /// <summary>Deletes the header first, so a journal half-deleted is no journal at all.</summary>
    private async Task DeleteJournalAsync(int parts, CancellationToken cancellationToken)
    {
        await _meta.DeleteAsync(_keys.Meta(JournalName), cancellationToken).ConfigureAwait(false);
        await StepAsync("unjournal").ConfigureAwait(false);
        foreach (var call in Enumerable.Range(0, parts).Chunk(JournalPartsPerCall))
        {
            await _meta.DeleteAsync(call.Select(i => _keys.Meta(JournalPart(i))), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string JournalPart(int index) => $"{JournalName}{index}";

    /// <summary>Writes a change set's records, then its metadata last. Every write is an upsert by key, so writing twice is harmless.</summary>
    private async Task WriteAsync(ChangeSet changes, CancellationToken cancellationToken)
    {
        foreach (var page in changes.Clusters.Chunk(_clusterPage))
        {
            await _clusters.UpsertAsync(page.Select(ToRecord), cancellationToken).ConfigureAwait(false);
        }

        await StepAsync("clusters").ConfigureAwait(false);
        foreach (var page in changes.RemovedItemIds.Chunk(_options.PageSize))
        {
            await _items.DeleteAsync(page.Select(_keys.Item), cancellationToken).ConfigureAwait(false);
        }

        foreach (var page in changes.Items.Chunk(_itemPage))
        {
            await _items.UpsertAsync(page.Select(ToRecord), cancellationToken).ConfigureAwait(false);
        }

        await StepAsync("items").ConfigureAwait(false);
        foreach (var page in changes.Questions.Chunk(_options.PageSize))
        {
            await _questions.UpsertAsync(page.Select(ToRecord), cancellationToken).ConfigureAwait(false);
        }

        foreach (var page in changes.MergeChecks.Chunk(_options.PageSize))
        {
            await _checks.UpsertAsync(page.Select(ToRecord), cancellationToken).ConfigureAwait(false);
        }

        await StepAsync("questions").ConfigureAwait(false);
        await WriteInfoAsync(changes.Info, cancellationToken).ConfigureAwait(false);
        await StepAsync("info").ConfigureAwait(false);
    }

    private Task WriteInfoAsync(StoreInfo info, CancellationToken cancellationToken) =>
        _meta.UpsertAsync(Meta(InfoName, JsonSerializer.Serialize(new StoredInfoEnvelope(SchemaVersion, info), StoreJson.Default.StoredInfoEnvelope)), cancellationToken);

    private async Task<StoreInfo> ReadInfoAsync(CancellationToken cancellationToken)
    {
        if (await _meta.GetAsync(_keys.Meta(InfoName), cancellationToken: cancellationToken).ConfigureAwait(false) is not { } record)
        {
            return StoreInfo.Empty;
        }

        var envelope = Read(record.Body, StoreJson.Default.StoredInfoEnvelope);
        return envelope.SchemaVersion > SchemaVersion
            ? throw new InvalidDataException($"The store was written by a newer Akinly (schema {envelope.SchemaVersion}); this version reads up to {SchemaVersion}.")
            : envelope.Info;
    }

    private Task StepAsync(string step) => OnStep?.Invoke(step) ?? Task.CompletedTask;

    /// <summary>A search widened to make up for an approximate index, without overflowing.</summary>
    private int Widen(int count) => (int)Math.Min(int.MaxValue, (long)count + _options.SearchOversampling);

    /// <summary>
    /// Every record matching a filter, in key order, a page at a time, up to <paramref name="limit"/>.
    /// Pages continue after the last key seen rather than skipping, so each page costs the same.
    /// </summary>
    private static async IAsyncEnumerable<TRecord> PageAsync<TRecord>(
        VectorStoreCollection<TKey, TRecord> collection,
        int pageSize,
        Func<long, Expression<Func<TRecord, bool>>> filterAfter,
        Expression<Func<TRecord, object?>> orderBy,
        Func<TRecord, long> keyOf,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        long after = 0,
        int limit = int.MaxValue,
        bool includeVectors = true)
        where TRecord : class
    {
        var taken = 0;
        while (taken < limit)
        {
            var take = Math.Min(pageSize, limit - taken);
            var options = new FilteredRecordRetrievalOptions<TRecord> { OrderBy = o => o.Ascending(orderBy), IncludeVectors = includeVectors };
            var page = 0;
            await foreach (var record in collection.GetAsync(filterAfter(after), take, options, cancellationToken).ConfigureAwait(false))
            {
                page++;
                after = keyOf(record);
                yield return record;
            }

            taken += page;
            if (page < take)
            {
                yield break;
            }
        }
    }

    // ---------------------------------------------------------------- mapping

    private ClusterRecord<TKey> ToRecord(StoredCluster cluster) => new()
    {
        Key = _keys.Cluster(cluster.Id),
        ClusterId = cluster.Id,
        Live = cluster.IsLive,
        Changed = cluster.Changed,
        Count = cluster.Count,
        Label = cluster.Label,
        LabeledAtCount = cluster.LabeledAtCount,
        RemovalsSinceRecompute = cluster.RemovalsSinceRecompute,
        MergedIntoId = cluster.MergedIntoId ?? 0,
        Sum = SumCodec.Encode(cluster.Sum.Span),
        RefusedBy = JsonSerializer.Serialize(cluster.RefusedBy.ToArray(), StoreJson.Default.Int64Array),
        Centroid = cluster.Centroid,
    };

    private static StoredCluster ToStored(ClusterRecord<TKey> record) => new(
        record.ClusterId,
        record.Centroid,
        SumCodec.Decode(record.Sum),
        record.Count,
        record.Label,
        record.MergedIntoId == 0 ? null : record.MergedIntoId,
        record.Changed,
        record.RemovalsSinceRecompute,
        Read(record.RefusedBy, StoreJson.Default.Int64Array),
        record.LabeledAtCount);

    private ItemRecord<TKey> ToRecord(StoredItem item) => new()
    {
        Key = _keys.Item(item.Id),
        ItemId = item.Id,
        Sequence = item.Sequence,
        ClusterId = item.ClusterId ?? 0,
        State = (int)item.State,
        RefusedClusterIds = JsonSerializer.Serialize(item.RefusedClusterIds.ToArray(), StoreJson.Default.Int64Array),
        OpenQuestionId = item.OpenQuestionId?.Value ?? 0,
        JudgeUnavailable = item.JudgeUnavailable,
        Embedding = item.Embedding,
    };

    private static StoredItem ToStored(ItemRecord<TKey> record) => new(
        record.ItemId,
        record.Sequence,
        record.Embedding,
        record.ClusterId == 0 ? null : record.ClusterId,
        (ItemState)record.State,
        Read(record.RefusedClusterIds, StoreJson.Default.Int64Array),
        record.OpenQuestionId == 0 ? null : new QuestionId(record.OpenQuestionId),
        record.JudgeUnavailable);

    private QuestionRecord<TKey> ToRecord(StoredQuestion question) => new()
    {
        Key = _keys.Question(question.Question.Id),
        QuestionId = question.Question.Id.Value,
        Open = question.State == QuestionState.Open,
        Body = JsonSerializer.Serialize(question, StoreJson.Default.StoredQuestion),
    };

    private CheckRecord<TKey> ToRecord(MergeCheck check) => new()
    {
        Key = _keys.Check(check.ClusterId, check.OtherClusterId),
        Body = JsonSerializer.Serialize(check, StoreJson.Default.MergeCheck),
    };

    private MetaRecord<TKey> Meta(string name, string body) => new() { Key = _keys.Meta(name), Name = name, Body = body };

    private static T Read<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(json, type) ?? throw new InvalidDataException($"A stored {typeof(T).Name} is empty.");

    // ---------------------------------------------------------------- collection definitions

    internal static VectorStoreCollectionDefinition ClusterDefinition(VectorDataClusterStoreOptions options) => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty(nameof(ClusterRecord<TKey>.Key), typeof(TKey)),
            Indexed(nameof(ClusterRecord<TKey>.ClusterId), typeof(long)),
            Indexed(nameof(ClusterRecord<TKey>.Live), typeof(bool)),
            Indexed(nameof(ClusterRecord<TKey>.Changed), typeof(bool)),
            new VectorStoreDataProperty(nameof(ClusterRecord<TKey>.Count), typeof(int)),
            new VectorStoreDataProperty(nameof(ClusterRecord<TKey>.Label), typeof(string)),
            new VectorStoreDataProperty(nameof(ClusterRecord<TKey>.LabeledAtCount), typeof(int)),
            new VectorStoreDataProperty(nameof(ClusterRecord<TKey>.RemovalsSinceRecompute), typeof(int)),
            new VectorStoreDataProperty(nameof(ClusterRecord<TKey>.MergedIntoId), typeof(long)),
            new VectorStoreDataProperty(nameof(ClusterRecord<TKey>.Sum), typeof(string)),
            new VectorStoreDataProperty(nameof(ClusterRecord<TKey>.RefusedBy), typeof(string)),
            Vector(options, nameof(ClusterRecord<TKey>.Centroid), options.Dimensions),
        ],
    };

    internal static VectorStoreCollectionDefinition ItemDefinition(VectorDataClusterStoreOptions options) => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty(nameof(ItemRecord<TKey>.Key), typeof(TKey)),
            new VectorStoreDataProperty(nameof(ItemRecord<TKey>.ItemId), typeof(string)),
            Indexed(nameof(ItemRecord<TKey>.Sequence), typeof(long)),
            Indexed(nameof(ItemRecord<TKey>.ClusterId), typeof(long)),
            Indexed(nameof(ItemRecord<TKey>.State), typeof(int)),
            new VectorStoreDataProperty(nameof(ItemRecord<TKey>.RefusedClusterIds), typeof(string)),
            new VectorStoreDataProperty(nameof(ItemRecord<TKey>.OpenQuestionId), typeof(long)),
            new VectorStoreDataProperty(nameof(ItemRecord<TKey>.JudgeUnavailable), typeof(bool)),
            Vector(options, nameof(ItemRecord<TKey>.Embedding), options.Dimensions),
        ],
    };

    internal static VectorStoreCollectionDefinition QuestionDefinition(VectorDataClusterStoreOptions options) => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty(nameof(QuestionRecord<TKey>.Key), typeof(TKey)),
            Indexed(nameof(QuestionRecord<TKey>.QuestionId), typeof(long)),
            Indexed(nameof(QuestionRecord<TKey>.Open), typeof(bool)),
            new VectorStoreDataProperty(nameof(QuestionRecord<TKey>.Body), typeof(string)),
            Vector(options, nameof(QuestionRecord<TKey>.Placeholder), 1),
        ],
    };

    internal static VectorStoreCollectionDefinition BodyDefinition(VectorDataClusterStoreOptions options) => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty(nameof(CheckRecord<TKey>.Key), typeof(TKey)),
            new VectorStoreDataProperty(nameof(CheckRecord<TKey>.Body), typeof(string)),
            Vector(options, nameof(CheckRecord<TKey>.Placeholder), 1),
        ],
    };

    internal static VectorStoreCollectionDefinition MetaDefinition(VectorDataClusterStoreOptions options) => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty(nameof(MetaRecord<TKey>.Key), typeof(TKey)),
            new VectorStoreDataProperty(nameof(MetaRecord<TKey>.Name), typeof(string)),
            new VectorStoreDataProperty(nameof(MetaRecord<TKey>.Body), typeof(string)),
            Vector(options, nameof(MetaRecord<TKey>.Placeholder), 1),
        ],
    };

    private static VectorStoreDataProperty Indexed(string name, Type type) => new(name, type) { IsIndexed = true };

    private static VectorStoreVectorProperty Vector(VectorDataClusterStoreOptions options, string name, int dimensions) => new(name, typeof(ReadOnlyMemory<float>), dimensions)
    {
        DistanceFunction = options.DistanceFunction,
        IndexKind = options.IndexKind,
    };
}
