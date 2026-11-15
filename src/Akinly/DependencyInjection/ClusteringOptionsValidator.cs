using Akinly;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Validates <see cref="ClusteringOptions"/> when the host starts, so a bad threshold fails the host, not a request.</summary>
internal sealed class ClusteringOptionsValidator : IValidateOptions<ClusteringOptions>
{
    public ValidateOptionsResult Validate(string? name, ClusteringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
