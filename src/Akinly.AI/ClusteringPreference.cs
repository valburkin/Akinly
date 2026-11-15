namespace Akinly.AI;

/// <summary>
/// Which mistake a <see cref="ChatClientJudge"/> should rather make when a question is a close
/// call: the tie-break it is told to use. Pair it with the threshold set the calibration tool
/// suggests for the same preference.
/// </summary>
public enum ClusteringPreference
{
    /// <summary>No tie-break: the model answers as it judges. The default.</summary>
    Balanced = 0,

    /// <summary>When unsure, keep apart: purer clusters, more of them, some duplicates left split.</summary>
    Precision = 1,

    /// <summary>When unsure, put together: fewer, larger clusters, some unlike items joined.</summary>
    Recall = 2,
}
