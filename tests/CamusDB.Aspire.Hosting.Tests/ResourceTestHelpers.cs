using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace CamusDB.Aspire.Hosting.Tests;

internal static class ResourceTestHelpers
{
    public static void AllocateEndpoints(IResourceWithEndpoints resource)
    {
        foreach (EndpointAnnotation endpoint in resource.Annotations.OfType<EndpointAnnotation>())
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", endpoint.TargetPort!.Value);
    }

    public static async Task<Dictionary<string, string?>> GetEnvironmentAsync(IResourceWithEnvironment resource)
    {
        Dictionary<string, object> values = [];
        EnvironmentCallbackContext context = new(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource, values);

        foreach (EnvironmentCallbackAnnotation callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await callback.Callback(context);

        Dictionary<string, string?> result = [];
        foreach ((string key, object value) in values)
            result[key] = value switch
            {
                string text => text,
                IValueProvider provider => await provider.GetValueAsync(CancellationToken.None),
                _ => value.ToString(),
            };

        return result;
    }

    public static async Task<IReadOnlyList<object>> GetArgsAsync(IResourceWithArgs resource)
    {
        List<object> args = [];
        CommandLineArgsCallbackContext context = new(args);

        foreach (CommandLineArgsCallbackAnnotation callback in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
            await callback.Callback(context);

        return args;
    }
}
