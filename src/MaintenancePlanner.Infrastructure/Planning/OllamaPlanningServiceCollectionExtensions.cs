using MaintenancePlanner.Application.Planning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MaintenancePlanner.Infrastructure.Planning;

public static class OllamaPlanningServiceCollectionExtensions
{
    public static IServiceCollection AddOllamaPlanningInterpreter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OllamaOptions>()
            .Bind(configuration.GetSection(OllamaOptions.SectionName))
            .Validate(options =>
                    Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) &&
                    (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps),
                "Ollama:BaseUrl must be an absolute HTTP or HTTPS URL.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Model), "Ollama:Model is required.")
            .Validate(options => options.TimeoutSeconds is > 0 and <= 600,
                "Ollama:TimeoutSeconds must be between 1 and 600.")
            .Validate(options => options.MaxToolCalls is > 0 and <= 20,
                "Ollama:MaxToolCalls must be between 1 and 20.")
            .ValidateOnStart();

        services.AddHttpClient<IPlanningRequestInterpreter, OllamaPlanningRequestInterpreter>(
            (serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
                client.BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/", UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            });
        return services;
    }
}
