using Akinly.Storage;

namespace Akinly.Engine;

/// <summary>
/// The working set of one clusterer call: it reads committed state from the store on demand,
/// applies every decision in memory, and turns what changed into one <see cref="ChangeSet"/>.
/// Not thread-safe; one session per call.
/// </summary>
internal sealed class Session
{
    private const int MaxRedirects = 1024;
    private const int RecomputeAfterRemovals = 16;

    private readonly IClusterStore _store;
    private readonly ClusteringOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly long _baseVersion;

    private readonly Dictionary<long, StoredCluster?> _clusters = [];
    private readonly HashSet<long> _dirtyClusters = [];

    // Changed clusters the store already holds. The store ranks them by their stale centroids,
    // so each may take a slot in its nearest-cluster results; clusters this session created are
    // not in the store and take none.
    private int _dirtyStoredClusters;
    private readonly Dictionary<string, StoredItem?> _items = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedItems = new(StringComparer.Ordinal);
    private readonly Dictionary<QuestionId, StoredQuestion?> _questions = [];
    private readonly HashSet<QuestionId> _dirtyQuestions = [];
    private readonly Dictionary<(long, long), MergeCheck?> _mergeChecks = [];
    private readonly HashSet<(long, long)> _dirtyMergeChecks = [];

    private readonly List<Placement> _placements = [];
    private readonly List<ClusterMerge> _merges = [];
    private readonly List<ClusterSplit> _splits = [];
    private readonly List<Question> _asked = [];

    private readonly StoreInfo _startInfo;
    private StoreInfo _info;
    private HashSet<(long, long)>? _openMergePairs;
    private readonly IMergePolicy? _mergePolicy;
    private readonly Dictionary<(long, long), MergeRule> _mergeRules = [];

    private Session(IClusterStore store, ClusteringOptions options, IMergePolicy? mergePolicy, StoreInfo info, CancellationToken cancellationToken)
    {
        _store = store;
        _options = options;
        _mergePolicy = mergePolicy;
        _info = info;
        _startInfo = info;
        _baseVersion = info.Version;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The questions asked in this session, by kind, for telemetry.</summary>
    public IReadOnlyList<Question> Asked => _asked;

    public static async Task<Session> StartAsync(IClusterStore store, ClusteringOptions options, IMergePolicy? mergePolicy, CancellationToken cancellationToken)
    {
        var info = await store.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        if (info.EmbeddingModel is { } model && !string.Equals(model, options.EmbeddingModel, StringComparison.Ordinal))
        {
            throw new EmbeddingModelMismatchException(
                $"The store holds embeddings from '{model}', but the clusterer is configured for '{options.EmbeddingModel}'. Thresholds do not carry over between models; use a separate store.");
        }

        return new Session(store, options, mergePolicy, info, cancellationToken);
    }

    // ---------------------------------------------------------------- operations

    public async Task AddAsync(IReadOnlyList<ClusterItem> items)
    {
        var prepared = new List<(string Id, float[] Unit)>(items.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null)
            {
                throw new ArgumentException("The list holds a null item.", nameof(items));
            }

            if (string.IsNullOrEmpty(item.Id))
            {
                throw new ArgumentException("An item has an empty id.", nameof(items));
            }

            if (!ids.Add(item.Id))
            {
                throw new ArgumentException($"Item '{item.Id}' appears more than once.", nameof(items));
            }

            var dimensions = _info.Dimensions == 0 ? (prepared.Count == 0 ? item.Embedding.Length : prepared[0].Unit.Length) : _info.Dimensions;
            if (item.Embedding.Length != dimensions)
            {
                throw new EmbeddingModelMismatchException(
                    $"Item '{item.Id}' has an embedding of {item.Embedding.Length} dimensions where the store holds {dimensions}.");
            }

            var unit = VectorMath.UnitOrNull(item.Embedding.Span)
                ?? throw new ArgumentException($"Item '{item.Id}' has an embedding that is zero or not finite; it cannot be clustered.", nameof(items));
            prepared.Add((item.Id, unit));
        }

        if (prepared.Count == 0)
        {
            return;
        }

        var existing = await _store.GetItemsAsync(ids, _cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            throw new ArgumentException($"Item '{existing[0].Id}' is already in the store; remove it before adding it again.", nameof(items));
        }

        if (_info.EmbeddingModel is null)
        {
            _info = _info with { EmbeddingModel = _options.EmbeddingModel, Dimensions = prepared[0].Unit.Length };
        }

        await DrainWaitingAsync().ConfigureAwait(false);
        foreach (var (id, unit) in prepared)
        {
            var item = new StoredItem(id, _info.NextItemSequence, unit, null, ItemState.Waiting, [], null, false);
            _info = _info with { NextItemSequence = _info.NextItemSequence + 1 };
            _items[id] = null;
            await PlaceAsync(item).ConfigureAwait(false);
        }
    }

    public async Task AnswerAsync(IEnumerable<Verdict> verdicts)
    {
        // Only questions that existed when the call began can be answered: an id handed out
        // later in this call must not pick up a stray answer meant for nothing.
        var first = new Dictionary<QuestionId, Verdict>();
        foreach (var verdict in verdicts)
        {
            if (verdict.QuestionId.Value > 0 && verdict.QuestionId.Value < _startInfo.NextQuestionId)
            {
                first.TryAdd(verdict.QuestionId, verdict);
            }
        }

        await LoadQuestionsAsync(first.Keys).ConfigureAwait(false);
        foreach (var id in first.Keys.Order())
        {
            if (_questions.GetValueOrDefault(id) is not { State: QuestionState.Open } stored)
            {
                continue;
            }

            switch (stored.Question)
            {
                case JoinQuestion join:
                    await AnswerJoinAsync(stored, join, first[id]).ConfigureAwait(false);
                    break;
                case MergeQuestion merge:
                    await AnswerMergeAsync(stored, merge, first[id].Kind).ConfigureAwait(false);
                    break;
                default:
                    break;
            }
        }

        await DrainWaitingAsync().ConfigureAwait(false);
    }

    public async Task MaintainAsync()
    {
        await DrainWaitingAsync().ConfigureAwait(false);

        // Continue after the last cluster the previous call reached, wrapping around, so with a
        // per-call limit no changed cluster waits forever behind others that keep changing.
        var limit = _options.MaxClustersPerMaintain;
        var cursor = _info.MaintainCursor;
        var changed = (await _store.GetChangedClustersAsync(cursor, limit, _cancellationToken).ConfigureAwait(false)).ToList();
        if (changed.Count < limit && cursor > 0)
        {
            var wrapped = await _store.GetChangedClustersAsync(0, limit - changed.Count, _cancellationToken).ConfigureAwait(false);
            changed.AddRange(wrapped.Where(c => c.Id <= cursor));
        }

        _info = _info with { MaintainCursor = changed.Count == 0 ? 0 : changed[^1].Id };
        var changedIds = changed.Select(c => c.Id).ToList();
        foreach (var cluster in changed)
        {
            _clusters.TryAdd(cluster.Id, cluster);
        }

        var touched = new HashSet<long>();
        var keepChanged = new HashSet<long>();

        // Candidate pairs: each changed cluster with its nearest neighbors. Empty clusters only
        // wait for a returning item; they are never merged or asked about.
        var pairs = new Dictionary<(long, long), double>();
        foreach (var id in changedIds)
        {
            if (await GetLiveAsync(id).ConfigureAwait(false) is not { Count: > 0 } cluster)
            {
                continue;
            }

            foreach (var (neighbor, similarity) in await CandidatesAsync(cluster.Centroid, _options.CandidateClusters, [cluster.Id]).ConfigureAwait(false))
            {
                if (neighbor.Count > 0)
                {
                    pairs.TryAdd(MergeCheck.Key(cluster.Id, neighbor.Id), similarity);
                }
            }
        }

        var ordered = pairs.OrderByDescending(p => p.Value).ThenBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2).ToList();
        var checks = await _store.GetMergeChecksAsync(ordered.Select(p => p.Key), _cancellationToken).ConfigureAwait(false);
        foreach (var check in checks)
        {
            _mergeChecks.TryAdd(MergeCheck.Key(check.ClusterId, check.OtherClusterId), check);
        }

        // Certain merges: each cluster merges at most once per call, most similar pairs first.
        // A pair a judge called different, or that a split just separated, stays apart.
        var used = new HashSet<long>();
        foreach (var ((a, b), _) in ordered.Where(p => p.Value >= _options.MergeSimilarity))
        {
            var x = await GetLiveAsync(a).ConfigureAwait(false);
            var y = await GetLiveAsync(b).ConfigureAwait(false);
            if (x is null || y is null || x.Id == y.Id || used.Contains(x.Id) || used.Contains(y.Id)
                || _mergeChecks.GetValueOrDefault(MergeCheck.Key(x.Id, y.Id)) is { Outcome: VerdictKind.Different }
                || await BlockedAsync(x, y).ConfigureAwait(false))
            {
                continue;
            }

            var into = await MergeAsync(x, y, PlacementReason.Similarity).ConfigureAwait(false);
            used.Add(x.Id);
            used.Add(y.Id);
            touched.Add(into);
        }

        // Close calls: ask a judge, once per pair.
        if (_options.MergeCheckSimilarity < _options.MergeSimilarity)
        {
            var openPairs = await OpenMergePairsAsync().ConfigureAwait(false);
            foreach (var ((a, b), similarity) in ordered.Where(p => p.Value >= _options.MergeCheckSimilarity && p.Value < _options.MergeSimilarity))
            {
                var x = await GetLiveAsync(a).ConfigureAwait(false);
                var y = await GetLiveAsync(b).ConfigureAwait(false);
                if (x is null || y is null || x.Id == y.Id || used.Contains(x.Id) || used.Contains(y.Id))
                {
                    continue;
                }

                var key = MergeCheck.Key(x.Id, y.Id);
                if (_mergeChecks.GetValueOrDefault(key) is not null || openPairs.Contains(key)
                    || await BlockedAsync(x, y).ConfigureAwait(false))
                {
                    continue;
                }

                if (_info.OpenQuestions >= _options.MaxOpenQuestions)
                {
                    keepChanged.Add(a);
                    keepChanged.Add(b);
                    continue;
                }

                await AskMergeAsync(x, y, similarity).ConfigureAwait(false);
                openPairs.Add(key);
            }
        }

        // Splits, for cosine-only setups. A cluster merged into in this call is left whole: the
        // merge and the split would only undo each other.
        if (_options.Split.Enabled)
        {
            foreach (var id in changedIds.Where(id => !used.Contains(id)).Order())
            {
                if (await GetLiveAsync(id).ConfigureAwait(false) is { } cluster && await SplitAsync(cluster).ConfigureAwait(false))
                {
                    touched.Add(cluster.Id);
                }
            }
        }

        foreach (var id in changedIds.Concat(touched).Distinct().Order())
        {
            if (await GetLiveAsync(id).ConfigureAwait(false) is { RemovalsSinceRecompute: >= RecomputeAfterRemovals } cluster)
            {
                await RecomputeAsync(cluster).ConfigureAwait(false);
            }
        }

        // Clusters checked here are settled until they change again; those merged into or
        // split here changed, so the next call checks them.
        foreach (var id in changedIds)
        {
            if (!touched.Contains(id) && !keepChanged.Contains(id) && await GetLiveAsync(id).ConfigureAwait(false) is { Changed: true } cluster)
            {
                PutCluster(cluster with { Changed = false });
            }
        }
    }

    public async Task RemoveAsync(IEnumerable<string> itemIds)
    {
        var ids = itemIds.Distinct(StringComparer.Ordinal).ToList();
        await LoadItemsAsync(ids).ConfigureAwait(false);
        foreach (var id in ids)
        {
            if (_items.GetValueOrDefault(id) is not { } item)
            {
                continue;
            }

            switch (item.State)
            {
                case ItemState.Placed when item.ClusterId is { } clusterId:
                    if (await GetAsync(clusterId).ConfigureAwait(false) is { } cluster)
                    {
                        var count = cluster.Count - 1;
                        var sum = count == 0 ? new double[cluster.Sum.Length] : VectorMath.Add(cluster.Sum.Span, item.Embedding.Span, -1);
                        PutCluster(cluster with
                        {
                            Sum = sum,
                            Count = count,
                            Centroid = count == 0 ? cluster.Centroid : VectorMath.Direction(sum, cluster.Centroid),
                            RemovalsSinceRecompute = count == 0 ? 0 : cluster.RemovalsSinceRecompute + 1,
                            Changed = true,
                        });
                    }

                    break;
                case ItemState.Asking when item.OpenQuestionId is { } questionId:
                    await LoadQuestionsAsync([questionId]).ConfigureAwait(false);
                    if (_questions.GetValueOrDefault(questionId) is { State: QuestionState.Open } open)
                    {
                        CloseQuestion(open, null);
                    }

                    break;
                case ItemState.Waiting:
                    _info = _info with { WaitingItems = _info.WaitingItems - 1 };
                    break;
                default:
                    break;
            }

            _items[id] = null;
            _dirtyItems.Remove(id);
            _removedItems.Add(id);
        }

        // A removed question frees a place for an item that waits to ask.
        await DrainWaitingAsync().ConfigureAwait(false);
    }

    public async Task<bool> SetLabelAsync(long clusterId, string? label)
    {
        if (await ResolveAsync(clusterId).ConfigureAwait(false) is not { } cluster)
        {
            return false;
        }

        var labeledAt = label is null ? 0 : cluster.Count;
        if (!string.Equals(cluster.Label, label, StringComparison.Ordinal) || cluster.LabeledAtCount != labeledAt)
        {
            PutCluster(cluster with { Label = label, LabeledAtCount = labeledAt });
        }

        return true;
    }

    public async Task<StoredCluster?> ResolveAsync(long clusterId)
    {
        var cluster = await GetAsync(clusterId).ConfigureAwait(false);
        for (var hops = 0; cluster is { MergedIntoId: { } next }; hops++)
        {
            if (hops >= MaxRedirects)
            {
                throw new InvalidOperationException($"Cluster {clusterId} redirects in a loop; the store is corrupt.");
            }

            cluster = await GetAsync(next).ConfigureAwait(false);
        }

        return cluster;
    }

    public ClusteringStep Step() => new([.. _placements], [.. _merges], [.. _splits], [.. _asked]);

    public ChangeSet? Changes()
    {
        var info = _info with { Version = _baseVersion + 1 };
        var changes = new ChangeSet(
            info,
            [.. _dirtyClusters.Order().Select(id => _clusters[id]!)],
            [.. _dirtyItems.Select(id => _items[id]!).OrderBy(i => i.Sequence)],
            [.. _removedItems.Order(StringComparer.Ordinal)],
            [.. _dirtyQuestions.Order().Select(id => _questions[id]!)],
            [.. _dirtyMergeChecks.Order().Select(key => _mergeChecks[key]!)]);
        return changes.IsEmpty && _info == _startInfo ? null : changes;
    }

    public long BaseVersion => _baseVersion;

    // ---------------------------------------------------------------- placing items

    /// <summary>Places an item that is not in a cluster: join, ask, wait, or start a cluster.</summary>
    /// <returns>False when the item had to wait for room to ask its question.</returns>
    private async Task<bool> PlaceAsync(StoredItem item)
    {
        var refused = await RefusersAsync(item).ConfigureAwait(false);
        var candidates = await CandidatesAsync(item.Embedding, _options.CandidateClusters, refused).ConfigureAwait(false);
        if (candidates.Count > 0 && candidates[0].Similarity >= _options.JoinSimilarity)
        {
            Join(item, candidates[0].Cluster, PlacementReason.Similarity);
            return true;
        }

        // An empty cluster takes a returning item only at the join threshold: with no members
        // to show, a judge would have nothing to compare.
        // A question offers no more clusters than the item has checks left, so a "none" never
        // takes it past MaxChecksPerItem.
        var room = Math.Max(1, Math.Min(_options.JoinChoices, _options.MaxChecksPerItem - item.RefusedClusterIds.Count));
        var offered = candidates.Where(c => c.Cluster.Count > 0).Take(room).ToList();
        var (best, similarity) = offered.FirstOrDefault();
        var mayAsk = _options.CheckSimilarity < _options.JoinSimilarity
            && !item.JudgeUnavailable
            && item.RefusedClusterIds.Count < _options.MaxChecksPerItem;
        if (best is not null && mayAsk && similarity >= _options.CheckSimilarity)
        {
            if (_info.OpenQuestions >= _options.MaxOpenQuestions)
            {
                PutItem(item with { State = ItemState.Waiting, ClusterId = null, OpenQuestionId = null });
                return false;
            }

            await AskJoinAsync(item, offered).ConfigureAwait(false);
            return true;
        }

        Create(item);
        return true;
    }

    /// <summary>
    /// The live clusters that refused an item: each cluster that refused it, followed through
    /// merges, so a refusal still holds after the refusing cluster merged into another.
    /// </summary>
    private async Task<HashSet<long>> RefusersAsync(StoredItem item)
    {
        var refusers = new HashSet<long>();
        foreach (var id in item.RefusedClusterIds)
        {
            refusers.Add(id);
            if (await ResolveAsync(id).ConfigureAwait(false) is { } live)
            {
                refusers.Add(live.Id);
            }
        }

        return refusers;
    }

    private void Join(StoredItem item, StoredCluster cluster, PlacementReason reason)
    {
        var sum = VectorMath.Add(cluster.Sum.Span, item.Embedding.Span);
        PutCluster(cluster with
        {
            Sum = sum,
            Count = cluster.Count + 1,
            Centroid = VectorMath.Direction(sum, cluster.Centroid),
            Changed = true,
            RefusedBy = Union(cluster.RefusedBy, item.RefusedClusterIds),
        });
        PutItem(item with { ClusterId = cluster.Id, State = ItemState.Placed, OpenQuestionId = null });
        _placements.Add(new Placement(item.Id, cluster.Id, reason));
    }

    private void Create(StoredItem item)
    {
        var id = _info.NextClusterId;
        _info = _info with { NextClusterId = id + 1 };
        var sum = VectorMath.Add([], item.Embedding.Span);
        PutCluster(new StoredCluster(id, item.Embedding, sum, 1, null, null, true, 0, [.. item.RefusedClusterIds.Order()]));
        PutItem(item with { ClusterId = id, State = ItemState.Placed, OpenQuestionId = null });
        _placements.Add(new Placement(item.Id, id, PlacementReason.NewCluster));
    }

    private async Task AskJoinAsync(StoredItem item, List<(StoredCluster Cluster, double Similarity)> offered)
    {
        var id = NextQuestionId();
        var (cluster, similarity) = offered[0];
        var examples = await ExamplesAsync(cluster, _options.ExamplesPerQuestion).ConfigureAwait(false);
        var others = new List<JoinChoice>();
        foreach (var (other, otherSimilarity) in offered.Skip(1))
        {
            // A choice with neither examples nor a label could not be shown to a judge, and
            // would make the whole question unanswerable: leave it out.
            var otherExamples = await ExamplesAsync(other, _options.ExamplesPerQuestion).ConfigureAwait(false);
            if (otherExamples.Count > 0 || other.Label is not null)
            {
                others.Add(new JoinChoice(other.Id, otherSimilarity, other.Label, otherExamples));
            }
        }

        var question = new JoinQuestion(id, item.Id, cluster.Id, similarity, cluster.Label, examples) { OtherChoices = others };
        OpenQuestion(question);
        PutItem(item with { State = ItemState.Asking, ClusterId = null, OpenQuestionId = id });
    }

    private async Task DrainWaitingAsync()
    {
        while (_info.WaitingItems > 0 && _info.OpenQuestions < _options.MaxOpenQuestions)
        {
            var size = (int)Math.Min(int.MaxValue, (long)_options.MaxOpenQuestions + _items.Count);
            var batch = await _store.GetWaitingItemsAsync(size, _cancellationToken).ConfigureAwait(false);
            var waiting = batch
                .Where(i => !_items.ContainsKey(i.Id))
                .Concat(_items.Values.OfType<StoredItem>().Where(i => i.State == ItemState.Waiting))
                .OrderBy(i => i.Sequence)
                .ToList();
            foreach (var stored in batch)
            {
                _items.TryAdd(stored.Id, stored);
            }

            if (waiting.Count == 0)
            {
                return;
            }

            foreach (var item in waiting)
            {
                if (_info.OpenQuestions >= _options.MaxOpenQuestions)
                {
                    return;
                }

                if (_items.GetValueOrDefault(item.Id) is { State: ItemState.Waiting } current && !await PlaceAsync(current).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
    }

    // ---------------------------------------------------------------- answers

    private async Task AnswerJoinAsync(StoredQuestion stored, JoinQuestion question, Verdict answer)
    {
        // A pick must be one of the clusters offered; anything else is no answer.
        var chosen = answer.PickedClusterId ?? question.ClusterId;
        var verdict = answer.Kind == VerdictKind.Same && chosen != question.ClusterId && !question.OtherChoices.Any(c => c.ClusterId == chosen)
            ? VerdictKind.Failed
            : answer.Kind;
        await LoadItemsAsync([question.ItemId]).ConfigureAwait(false);
        if (_items.GetValueOrDefault(question.ItemId) is not { State: ItemState.Asking } item || item.OpenQuestionId != question.Id)
        {
            CloseQuestion(stored, null);
            return;
        }

        switch (verdict)
        {
            case VerdictKind.Same:
                CloseQuestion(stored, VerdictKind.Same);

                // The cluster may have merged since the question was asked: the item goes to the
                // survivor only if it is still close to it and no cluster that refused the item
                // ended up in it.
                var target = await ResolveAsync(chosen).ConfigureAwait(false);
                if (target is not null
                    && !(await RefusersAsync(item).ConfigureAwait(false)).Contains(target.Id)
                    && (target.Id == chosen || VectorMath.Similarity(item.Embedding, target.Centroid) >= _options.CheckSimilarity))
                {
                    Join(item, target, PlacementReason.Judge);
                }
                else
                {
                    await PlaceAsync(item).ConfigureAwait(false);
                }

                break;
            case VerdictKind.Different:
                CloseQuestion(stored, VerdictKind.Different);
                await PlaceAsync(item with { RefusedClusterIds = [.. item.RefusedClusterIds, question.ClusterId, .. question.OtherChoices.Select(c => c.ClusterId)] }).ConfigureAwait(false);
                break;
            case VerdictKind.Unanswerable:
                CloseQuestion(stored, VerdictKind.Unanswerable);
                await PlaceAsync(item with { JudgeUnavailable = true }).ConfigureAwait(false);
                break;
            default:
                if (stored.FailedAttempts + 1 >= _options.FailedAnswers.MaxAttempts)
                {
                    CloseQuestion(stored with { FailedAttempts = stored.FailedAttempts + 1 }, VerdictKind.Failed);
                    await PlaceAsync(item with { JudgeUnavailable = true }).ConfigureAwait(false);
                }
                else
                {
                    PutQuestion(stored with { FailedAttempts = stored.FailedAttempts + 1 });
                }

                break;
        }
    }

    private async Task AnswerMergeAsync(StoredQuestion stored, MergeQuestion question, VerdictKind verdict)
    {
        var key = MergeCheck.Key(question.ClusterId, question.OtherClusterId);
        if (verdict == VerdictKind.Failed && stored.FailedAttempts + 1 < _options.FailedAnswers.MaxAttempts)
        {
            PutQuestion(stored with { FailedAttempts = stored.FailedAttempts + 1 });
            return;
        }

        CloseQuestion(stored with { FailedAttempts = stored.FailedAttempts + (verdict == VerdictKind.Failed ? 1 : 0) }, verdict);
        if (verdict == VerdictKind.Failed)
        {
            // No answer is not a refusal: the pair is not recorded, so it is asked again once
            // either cluster changes.
            return;
        }

        if (verdict == VerdictKind.Unanswerable)
        {
            // It can never be answered (a text is gone): settle the pair, so it costs no more
            // calls; a certain merge can still join the two.
            PutMergeCheck(new MergeCheck(key.Item1, key.Item2, VerdictKind.Unanswerable));
            return;
        }

        PutMergeCheck(new MergeCheck(key.Item1, key.Item2, verdict));
        if (verdict != VerdictKind.Same)
        {
            return;
        }

        // A late yes is checked: the pair must still be close, so a chain of yeses cannot fold
        // distant clusters together.
        var x = await ResolveAsync(question.ClusterId).ConfigureAwait(false);
        var y = await ResolveAsync(question.OtherClusterId).ConfigureAwait(false);
        if (x is not null && y is not null && x.Id != y.Id
            && VectorMath.Similarity(x.Centroid, y.Centroid) >= _options.MergeCheckSimilarity
            && !await BlockedAsync(x, y).ConfigureAwait(false))
        {
            await MergeAsync(x, y, PlacementReason.Judge).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- merges and splits

    private async Task AskMergeAsync(StoredCluster x, StoredCluster y, double similarity)
    {
        var (first, second) = x.Id < y.Id ? (x, y) : (y, x);
        var id = NextQuestionId();
        var question = new MergeQuestion(
            id,
            first.Id,
            second.Id,
            similarity,
            first.Label,
            second.Label,
            await ExamplesAsync(first, _options.MergeExamplesPerCluster).ConfigureAwait(false),
            await ExamplesAsync(second, _options.MergeExamplesPerCluster).ConfigureAwait(false));
        OpenQuestion(question);
    }

    /// <summary>
    /// Merges two live clusters into the one the merge policy keeps, or else the larger (on a tie,
    /// the lower id); returns its id.
    /// </summary>
    private async Task<long> MergeAsync(StoredCluster x, StoredCluster y, PlacementReason reason)
    {
        var (low, high) = x.Id < y.Id ? (x, y) : (y, x);
        var (target, source) = await RuleAsync(x, y).ConfigureAwait(false) switch
        {
            MergeRule.KeepFirst => (low, high),
            MergeRule.KeepSecond => (high, low),
            _ => x.Count > y.Count || (x.Count == y.Count && x.Id < y.Id) ? (x, y) : (y, x),
        };
        var members = await MembersAsync(source.Id).ConfigureAwait(false);
        var sum = VectorMath.Add(target.Sum.Span, source.Sum.Span);
        PutCluster(target with
        {
            Sum = sum,
            Count = target.Count + source.Count,
            Centroid = VectorMath.Direction(sum, target.Centroid),
            Label = target.Label ?? source.Label,
            LabeledAtCount = target.Label is null ? source.LabeledAtCount : target.LabeledAtCount,
            Changed = true,
            RemovalsSinceRecompute = target.RemovalsSinceRecompute + source.RemovalsSinceRecompute,
            RefusedBy = Union(target.RefusedBy, source.RefusedBy),
        });
        PutCluster(source with { MergedIntoId = target.Id, Count = 0, Sum = new double[source.Sum.Length], Changed = false });
        foreach (var member in members)
        {
            PutItem(member with { ClusterId = target.Id });
        }

        _merges.Add(new ClusterMerge(source.Id, target.Id, reason));
        return target.Id;
    }

    private async Task<bool> SplitAsync(StoredCluster cluster)
    {
        var split = _options.Split;
        if (cluster.Count < 2 * split.MinPartSize)
        {
            return false;
        }

        var members = await MembersAsync(cluster.Id).ConfigureAwait(false);
        var parts = IncrementalClusterer.TrySplit(
            [.. members.Select(m => new EmbeddedItem(m.Sequence, m.Embedding.ToArray()))],
            split.CohesionThreshold,
            split.RegroupSimilarity,
            split.MinPartSize);
        if (parts is null)
        {
            return false;
        }

        var bySequence = members.ToDictionary(m => m.Sequence);
        var newIds = new List<long>();
        for (var p = 0; p < parts.Count; p++)
        {
            var partMembers = parts[p].ItemIds.Select(sequence => bySequence[sequence]).ToList();
            var sum = VectorMath.Sum(partMembers.Select(m => m.Embedding), cluster.Sum.Length);
            var centroid = VectorMath.Direction(sum, cluster.Centroid);
            long id;
            if (p == 0)
            {
                id = cluster.Id;
                // The part keeps the label for now, but a labeler should write a new one: it describes the whole.
                PutCluster(cluster with { Sum = sum, Count = partMembers.Count, Centroid = centroid, Changed = true, RemovalsSinceRecompute = 0, LabeledAtCount = 0 });
            }
            else
            {
                id = _info.NextClusterId;
                _info = _info with { NextClusterId = id + 1 };
                newIds.Add(id);
                PutCluster(new StoredCluster(id, centroid, sum, partMembers.Count, null, null, true, 0, cluster.RefusedBy));
            }

            foreach (var member in partMembers.Where(m => m.ClusterId != id))
            {
                PutItem(member with { ClusterId = id });
            }
        }

        // The parts were split apart on purpose: record them as different, so the next merge
        // check does not put them straight back together.
        var partIds = newIds.Prepend(cluster.Id).ToList();
        for (var i = 0; i < partIds.Count; i++)
        {
            for (var j = i + 1; j < partIds.Count; j++)
            {
                var key = MergeCheck.Key(partIds[i], partIds[j]);
                PutMergeCheck(new MergeCheck(key.Item1, key.Item2, VerdictKind.Different));
            }
        }

        _splits.Add(new ClusterSplit(cluster.Id, newIds));
        return true;
    }

    private async Task RecomputeAsync(StoredCluster cluster)
    {
        var members = await MembersAsync(cluster.Id).ConfigureAwait(false);
        var sum = VectorMath.Sum(members.Select(m => m.Embedding), cluster.Sum.Length);
        PutCluster(cluster with
        {
            Sum = sum,
            Count = members.Count,
            Centroid = members.Count == 0 ? cluster.Centroid : VectorMath.Direction(sum, cluster.Centroid),
            RemovalsSinceRecompute = 0,
        });
    }

    /// <summary>
    /// Whether a merge of two clusters is skipped: the merge policy forbids it, or one refused a
    /// member of the other.
    /// </summary>
    private async Task<bool> BlockedAsync(StoredCluster x, StoredCluster y)
    {
        if (await RuleAsync(x, y).ConfigureAwait(false) == MergeRule.Never)
        {
            return true;
        }

        if (!_options.MergeRespectsRefusals)
        {
            return false;
        }

        return await RefusesAsync(x, y.Id).ConfigureAwait(false) || await RefusesAsync(y, x.Id).ConfigureAwait(false);
    }

    /// <summary>The merge policy's rule for a pair, asked once per pair in a call.</summary>
    private async Task<MergeRule> RuleAsync(StoredCluster x, StoredCluster y)
    {
        if (_mergePolicy is null)
        {
            return MergeRule.Allow;
        }

        var key = MergeCheck.Key(x.Id, y.Id);
        if (!_mergeRules.TryGetValue(key, out var rule))
        {
            var (first, second) = x.Id < y.Id ? (x, y) : (y, x);
            rule = await _mergePolicy.DecideAsync(first.ToCluster(), second.ToCluster(), _cancellationToken).ConfigureAwait(false);
            if (!Enum.IsDefined(rule))
            {
                throw new InvalidOperationException($"The merge policy answered {(int)rule}, which is no {nameof(MergeRule)}.");
            }

            _mergeRules[key] = rule;
        }

        return rule;
    }

    private async Task<bool> RefusesAsync(StoredCluster cluster, long otherId)
    {
        foreach (var refuser in cluster.RefusedBy)
        {
            if (refuser == otherId || (await ResolveAsync(refuser).ConfigureAwait(false))?.Id == otherId)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// The live clusters nearest a vector, as this session sees them (its own changes included),
    /// most similar first (ties by id), leaving out <paramref name="exclude"/>.
    /// </summary>
    private async Task<List<(StoredCluster Cluster, double Similarity)>> CandidatesAsync(
        ReadOnlyMemory<float> vector, int count, HashSet<long> exclude)
    {
        var fetched = await _store.NearestClustersAsync(vector, count + exclude.Count + _dirtyStoredClusters, _cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<long>();
        var candidates = new List<(StoredCluster, double)>();
        foreach (var neighbor in fetched)
        {
            _clusters.TryAdd(neighbor.Cluster.Id, neighbor.Cluster);
            Consider(neighbor.Cluster.Id);
        }

        foreach (var id in _dirtyClusters)
        {
            Consider(id);
        }

        return [.. candidates.OrderByDescending(c => c.Item2).ThenBy(c => c.Item1.Id).Take(count)];

        void Consider(long id)
        {
            if (exclude.Contains(id) || !seen.Add(id) || _clusters.GetValueOrDefault(id) is not { IsLive: true } cluster)
            {
                return;
            }

            candidates.Add((cluster, VectorMath.Similarity(vector, cluster.Centroid)));
        }
    }

    /// <summary>A cluster's members nearest its centroid, as this session sees them.</summary>
    private async Task<IReadOnlyList<string>> ExamplesAsync(StoredCluster cluster, int count)
    {
        if (count == 0)
        {
            return [];
        }

        var committed = await _store.GetExamplesAsync(cluster.Id, count, _cancellationToken).ConfigureAwait(false);
        await LoadItemsAsync(committed).ConfigureAwait(false);
        return [.. committed
            .Select(id => _items.GetValueOrDefault(id))
            .Concat(_items.Values.Where(i => i is { State: ItemState.Placed } && i.ClusterId == cluster.Id))
            .OfType<StoredItem>()
            .Where(i => i.State == ItemState.Placed && i.ClusterId == cluster.Id)
            .DistinctBy(i => i.Id)
            .OrderByDescending(i => VectorMath.Similarity(i.Embedding, cluster.Centroid))
            .ThenBy(i => i.Sequence)
            .Take(count)
            .Select(i => i.Id)];
    }

    /// <summary>A cluster's members, as this session sees them, by sequence.</summary>
    private async Task<List<StoredItem>> MembersAsync(long clusterId)
    {
        var committed = await _store.GetMembersAsync(clusterId, _cancellationToken).ConfigureAwait(false);
        foreach (var item in committed)
        {
            _items.TryAdd(item.Id, item);
        }

        return [.. committed
            .Select(i => i.Id)
            .Concat(_items.Values.OfType<StoredItem>().Where(i => i.ClusterId == clusterId).Select(i => i.Id))
            .Distinct(StringComparer.Ordinal)
            .Select(id => _items[id])
            .OfType<StoredItem>()
            .Where(i => i.State == ItemState.Placed && i.ClusterId == clusterId)
            .OrderBy(i => i.Sequence)];
    }

    private async Task<StoredCluster?> GetAsync(long id)
    {
        if (!_clusters.TryGetValue(id, out var cluster))
        {
            var found = await _store.GetClustersAsync([id], _cancellationToken).ConfigureAwait(false);
            cluster = found.Count > 0 ? found[0] : null;
            _clusters[id] = cluster;
        }

        return cluster;
    }

    private async Task<StoredCluster?> GetLiveAsync(long id) =>
        await GetAsync(id).ConfigureAwait(false) is { IsLive: true } cluster ? cluster : null;

    private async Task LoadItemsAsync(IEnumerable<string> ids)
    {
        var missing = ids.Where(id => !_items.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var found = (await _store.GetItemsAsync(missing, _cancellationToken).ConfigureAwait(false)).ToDictionary(i => i.Id, StringComparer.Ordinal);
        foreach (var id in missing)
        {
            _items[id] = found.GetValueOrDefault(id);
        }
    }

    private async Task LoadQuestionsAsync(IEnumerable<QuestionId> ids)
    {
        var missing = ids.Where(id => !_questions.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var found = (await _store.GetQuestionsAsync(missing, _cancellationToken).ConfigureAwait(false)).ToDictionary(q => q.Question.Id);
        foreach (var id in missing)
        {
            _questions[id] = found.GetValueOrDefault(id);
        }
    }

    private async Task<HashSet<(long, long)>> OpenMergePairsAsync()
    {
        if (_openMergePairs is null)
        {
            var open = await _store.GetOpenQuestionsAsync(_cancellationToken).ConfigureAwait(false);
            // By the pair's live clusters now, so a merge's survivors are not asked about again
            // while the question about their predecessors is still open.
            _openMergePairs = [];
            foreach (var question in open
                .Select(q => _questions.TryGetValue(q.Question.Id, out var mine) ? mine : q)
                .Where(q => q is { State: QuestionState.Open, Question: MergeQuestion })
                .Select(q => (MergeQuestion)q!.Question))
            {
                var x = await ResolveAsync(question.ClusterId).ConfigureAwait(false);
                var y = await ResolveAsync(question.OtherClusterId).ConfigureAwait(false);
                _openMergePairs.Add(MergeCheck.Key(x?.Id ?? question.ClusterId, y?.Id ?? question.OtherClusterId));
            }
        }

        return _openMergePairs;
    }

    // ---------------------------------------------------------------- writing

    private QuestionId NextQuestionId()
    {
        var id = new QuestionId(_info.NextQuestionId);
        _info = _info with { NextQuestionId = _info.NextQuestionId + 1 };
        return id;
    }

    private void OpenQuestion(Question question)
    {
        PutQuestion(new StoredQuestion(question, QuestionState.Open, 0, null));
        _info = _info with { OpenQuestions = _info.OpenQuestions + 1 };
        _asked.Add(question);
    }

    private void CloseQuestion(StoredQuestion question, VerdictKind? outcome)
    {
        if (question.State == QuestionState.Open)
        {
            _info = _info with { OpenQuestions = _info.OpenQuestions - 1 };
        }

        PutQuestion(question with { State = QuestionState.Closed, Outcome = outcome });
    }

    private void PutQuestion(StoredQuestion question)
    {
        _questions[question.Question.Id] = question;
        _dirtyQuestions.Add(question.Question.Id);
    }

    private void PutCluster(StoredCluster cluster)
    {
        _clusters[cluster.Id] = cluster;
        if (_dirtyClusters.Add(cluster.Id) && cluster.Id < _startInfo.NextClusterId)
        {
            _dirtyStoredClusters++;
        }
    }

    private void PutItem(StoredItem item)
    {
        var previous = _items.GetValueOrDefault(item.Id);
        var wasWaiting = previous?.State == ItemState.Waiting;
        var isWaiting = item.State == ItemState.Waiting;
        if (wasWaiting != isWaiting)
        {
            _info = _info with { WaitingItems = _info.WaitingItems + (isWaiting ? 1 : -1) };
        }

        _items[item.Id] = item;
        _dirtyItems.Add(item.Id);
    }

    private void PutMergeCheck(MergeCheck check)
    {
        var key = MergeCheck.Key(check.ClusterId, check.OtherClusterId);
        _mergeChecks[key] = check;
        _dirtyMergeChecks.Add(key);
    }

    private static long[] Union(IReadOnlyList<long> a, IReadOnlyList<long> b) =>
        b.Count == 0 ? [.. a] : [.. a.Union(b).Order()];
}
