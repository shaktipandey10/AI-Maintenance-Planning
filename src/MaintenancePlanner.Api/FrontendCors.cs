namespace MaintenancePlanner.Api;

public static class FrontendCors
{
    public const string PolicyName = "AllowFrontend";

    // The browser frontend runs on another origin, so it can read only CORS-safelisted response headers unless more
    // are exposed. Approval returns the draft ID solely in the Location header of its 202 response.
    public static IServiceCollection AddFrontendCors(this IServiceCollection services) =>
        services.AddCors(options =>
        {
            options.AddPolicy(PolicyName, policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .WithExposedHeaders("Location");
            });
        });
}
