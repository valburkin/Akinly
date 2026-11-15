using System.Globalization;
using Akinly.Evaluation;

// akinly-eval: measures Akinly on public labeled datasets, and suggests thresholds for an
// embedding model. Run without arguments for the usage.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return await Cli.RunAsync(args, cancel.Token);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    Console.Error.WriteLine("Stopped. Results of the setups that finished are saved.");
    return 130;
}
catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or HttpRequestException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
