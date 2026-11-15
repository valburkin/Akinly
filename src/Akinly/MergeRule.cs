namespace Akinly;

/// <summary>What an <see cref="IMergePolicy"/> allows for a pair of clusters.</summary>
public enum MergeRule
{
    /// <summary>They may merge; the larger keeps its id (on a tie, the lower id). The default.</summary>
    Allow = 0,

    /// <summary>They never merge, whatever their similarity or a judge's answer; no merge question is asked.</summary>
    Never = 1,

    /// <summary>They may merge, and the first (the lower id) keeps its id.</summary>
    KeepFirst = 2,

    /// <summary>They may merge, and the second (the higher id) keeps its id.</summary>
    KeepSecond = 3,
}
