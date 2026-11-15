namespace Akinly;

/// <summary>
/// The id of a question the clusterer put to a judge. Ids are handed out in order by the store,
/// so an answer finds its question after any wait, and answers apply in question order.
/// </summary>
/// <param name="Value">The id's number.</param>
public readonly record struct QuestionId(long Value) : IComparable<QuestionId>
{
    /// <inheritdoc />
    public int CompareTo(QuestionId other) => Value.CompareTo(other.Value);

    /// <summary>Whether <paramref name="left"/> comes before <paramref name="right"/>.</summary>
    public static bool operator <(QuestionId left, QuestionId right) => left.Value < right.Value;

    /// <summary>Whether <paramref name="left"/> comes after <paramref name="right"/>.</summary>
    public static bool operator >(QuestionId left, QuestionId right) => left.Value > right.Value;

    /// <summary>Whether <paramref name="left"/> comes before or is <paramref name="right"/>.</summary>
    public static bool operator <=(QuestionId left, QuestionId right) => left.Value <= right.Value;

    /// <summary>Whether <paramref name="left"/> comes after or is <paramref name="right"/>.</summary>
    public static bool operator >=(QuestionId left, QuestionId right) => left.Value >= right.Value;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
