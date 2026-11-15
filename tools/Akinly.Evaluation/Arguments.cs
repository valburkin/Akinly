using System.Globalization;

namespace Akinly.Evaluation;

/// <summary>
/// Command-line arguments: a command, then <c>--name value</c> pairs. Every option takes a
/// value; an unknown option, a missing value or a value of the wrong type is an
/// <see cref="ArgumentException"/>, never silently ignored.
/// </summary>
public sealed class Arguments
{
    private readonly Dictionary<string, string> _values;

    private Arguments(string? command, Dictionary<string, string> values) => (Command, _values) = (command, values);

    public string? Command { get; }

    public static Arguments Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i].Length == 2)
            {
                throw new ArgumentException($"Expected an option such as --dataset, not '{args[i]}'.");
            }

            var name = args[i][2..];
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"--{name} needs a value.");
            }

            if (!values.TryAdd(name, args[++i]))
            {
                throw new ArgumentException($"--{name} is given twice.");
            }
        }

        return new Arguments(args.Count > 0 ? args[0] : null, values);
    }

    /// <summary>Refuses any option outside <paramref name="known"/>.</summary>
    public void Allow(params string[] known)
    {
        var unknown = _values.Keys.Where(k => !known.Contains(k, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown option {string.Join(", ", unknown.Select(u => "--" + u))} for '{Command}'. Run without arguments for the usage.");
        }
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string Get(string name, string fallback) => _values.GetValueOrDefault(name, fallback);

    public int Number(string name, int fallback, int minimum = 0) =>
        !_values.TryGetValue(name, out var text) ? fallback
        : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= minimum ? value
        : throw new ArgumentException($"--{name} must be a whole number of at least {minimum}, not '{text}'.");

    /// <summary>A similarity threshold: above 0 and at most 1.</summary>
    public double Similarity(string name) =>
        double.TryParse(Get(name, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 && value <= 1
            ? value
            : throw new ArgumentException($"--{name} must be a number above 0 and at most 1, not '{Get(name, "")}'.");
}
