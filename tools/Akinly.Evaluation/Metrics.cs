namespace Akinly.Evaluation;

/// <summary>
/// Standard external clustering measures, from the contingency table of labels and clusters.
/// The adjusted Rand index, NMI and the V-measure family follow scikit-learn, its special cases
/// included; pairwise precision and recall count pairs, and are 1 when there are none.
/// </summary>
public static class Metrics
{
    public static Scores Score(IReadOnlyList<string> labels, IReadOnlyList<long> clusters)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(labels.Count, clusters.Count);
        var n = (double)labels.Count;
        var cells = new Dictionary<(string, long), long>();
        var byLabel = new Dictionary<string, long>(StringComparer.Ordinal);
        var byCluster = new Dictionary<long, long>();
        for (var i = 0; i < labels.Count; i++)
        {
            cells[(labels[i], clusters[i])] = cells.GetValueOrDefault((labels[i], clusters[i])) + 1;
            byLabel[labels[i]] = byLabel.GetValueOrDefault(labels[i]) + 1;
            byCluster[clusters[i]] = byCluster.GetValueOrDefault(clusters[i]) + 1;
        }

        static double Pairs(double count) => count * (count - 1) / 2;
        var together = cells.Values.Sum(c => Pairs(c));
        var labelPairs = byLabel.Values.Sum(c => Pairs(c));
        var clusterPairs = byCluster.Values.Sum(c => Pairs(c));
        var all = Pairs(n);

        // Fewer than two items, or a partition the same as the labels' trivially (all together,
        // or all apart, on both sides): a perfect match, as in scikit-learn.
        double ari;
        if (all == 0 || (byLabel.Count == byCluster.Count && (byLabel.Count == 1 || byLabel.Count == labels.Count)))
        {
            ari = 1;
        }
        else
        {
            var expected = labelPairs * clusterPairs / all;
            var maximum = (labelPairs + clusterPairs) / 2;
            ari = maximum == expected ? 1 : (together - expected) / (maximum - expected);
        }

        double Entropy(IEnumerable<long> counts) => -counts.Sum(c => c / n * Math.Log(c / n));
        var hLabels = n == 0 ? 0 : Entropy(byLabel.Values);
        var hClusters = n == 0 ? 0 : Entropy(byCluster.Values);
        var hLabelsGivenClusters = -cells.Sum(cell => cell.Value / n * Math.Log((double)cell.Value / byCluster[cell.Key.Item2]));
        var hClustersGivenLabels = -cells.Sum(cell => cell.Value / n * Math.Log((double)cell.Value / byLabel[cell.Key.Item1]));
        var mutual = Math.Max(0, hLabels - hLabelsGivenClusters);
        var homogeneity = hLabels == 0 ? 1 : 1 - (hLabelsGivenClusters / hLabels);
        var completeness = hClusters == 0 ? 1 : 1 - (hClustersGivenLabels / hClusters);
        var v = homogeneity + completeness == 0 ? 0 : 2 * homogeneity * completeness / (homogeneity + completeness);
        var nmi = hLabels == 0 && hClusters == 0 ? 1 : hLabels == 0 || hClusters == 0 ? 0 : mutual / ((hLabels + hClusters) / 2);

        var precision = clusterPairs == 0 ? 1 : together / clusterPairs;
        var recall = labelPairs == 0 ? 1 : together / labelPairs;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        var accuracy = n == 0 ? 1 : Matched(cells, [.. byLabel.Keys], [.. byCluster.Keys]) / n;
        return new Scores(ari, accuracy, nmi, homogeneity, completeness, v, precision, recall, f1, byCluster.Count, byCluster.Values.Count(c => c == 1));
    }

    /// <summary>
    /// The most items a one-to-one matching of labels to clusters can cover: the Hungarian
    /// algorithm (Jonker-Volgenant form) on the contingency table, padded to a square.
    /// </summary>
    private static double Matched(Dictionary<(string, long), long> cells, List<string> labels, List<long> clusters)
    {
        var size = Math.Max(labels.Count, clusters.Count);
        var cost = new double[size + 1, size + 1];
        for (var i = 0; i < labels.Count; i++)
        {
            for (var j = 0; j < clusters.Count; j++)
            {
                cost[i + 1, j + 1] = -cells.GetValueOrDefault((labels[i], clusters[j]));
            }
        }

        // Potentials u, v; p[j] is the row matched to column j; way[j] the previous column on the path.
        var u = new double[size + 1];
        var v = new double[size + 1];
        var p = new int[size + 1];
        var way = new int[size + 1];
        for (var i = 1; i <= size; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minimum = new double[size + 1];
            var used = new bool[size + 1];
            Array.Fill(minimum, double.PositiveInfinity);
            do
            {
                used[j0] = true;
                var (i0, delta, j1) = (p[j0], double.PositiveInfinity, 0);
                for (var j = 1; j <= size; j++)
                {
                    if (!used[j])
                    {
                        var current = cost[i0, j] - u[i0] - v[j];
                        if (current < minimum[j])
                        {
                            (minimum[j], way[j]) = (current, j0);
                        }

                        if (minimum[j] < delta)
                        {
                            (delta, j1) = (minimum[j], j);
                        }
                    }
                }

                for (var j = 0; j <= size; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minimum[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        var total = 0.0;
        for (var j = 1; j <= size; j++)
        {
            total -= cost[p[j], j];
        }

        return total;
    }
}
