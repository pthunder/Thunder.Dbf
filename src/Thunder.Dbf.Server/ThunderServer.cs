//    request ──> [ exceptions -> ProblemDetails ]      ProjectThunder :: Thunder.Dbf
//                [ HTTPS (in production)        ]      The pipeline, outside in. A request
//                [ rate limit, per address      ]      gets a ProblemDetails back from any
//                [ who are you? (X-Api-Key)     ]      layer that turns it away, and only
//                [ rate limit, per key          ]      reaches a table once it's through
//                [ may you? (authenticated)     ]      every one of them.
//                [ /tables/... ──> TableStore   ]

using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace Thunder.Dbf.Server;

/// <summary>Builds the web application: services, middleware and routes.</summary>
public static class ThunderServer
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // As a Windows service the working folder is System32; look for appsettings.json next to the exe instead.
            ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
        });
        builder.Host.UseWindowsService(options => options.ServiceName = "Thunder.Dbf");
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 16 * 1024 * 1024; // memos can be big; whole tables can't be posted
        });

        AddServices(builder.Services, builder.Configuration);

        var app = builder.Build();
        StartUp(app);

        app.UseExceptionHandler();
        app.UseStatusCodePages(); // bodiless 404s and 405s become ProblemDetails too
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }
        app.UseRateLimiter(); // per address first (before any key is hashed), then per key after authentication
        app.UseAuthentication();
        app.UseMiddleware<PerKeyRateLimit>();
        app.UseAuthorization();

        app.MapOpenApi().AllowAnonymous();
        app.MapThunderApi();
        return app;
    }

    private static void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ThunderOptions>().Bind(configuration.GetSection(ThunderOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ThunderOptions>, ThunderOptionsValidator>();

        services.AddSingleton<AccessRegistry>();
        services.AddSingleton<TableStore>();
        services.AddSingleton<WriteJournal>();
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<TableStore>();
            var journal = sp.GetRequiredService<WriteJournal>();
            var environment = sp.GetRequiredService<IHostEnvironment>();
            return new Scrubber([store.Folder, store.BackupFolder, Path.GetDirectoryName(journal.Path), environment.ContentRootPath, AppContext.BaseDirectory]);
        });

        // Every problem carries a machine-readable code, including the framework's own 404s and 405s.
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            if (context.ProblemDetails.Status is { } status)
                context.ProblemDetails.Extensions.TryAdd("code", ((System.Net.HttpStatusCode)status).ToString());
        });
        services.AddExceptionHandler<ProblemExceptionHandler>();

        // API keys today. An OpenID Connect scheme can be added beside this one later; permissions
        // come from AccessRegistry by key id, so endpoints won't need to change.
        services.AddAuthentication(ApiKeyAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthentication>(ApiKeyAuthentication.SchemeName, null);
        services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(AddressPartition);
            options.OnRejected = (context, _) => PerKeyRateLimit.RejectAsync(context.HttpContext, context.Lease, "This address has sent too many requests this minute.");
        });
        services.AddSingleton(PartitionedRateLimiter.Create<HttpContext, string>(PartitionFor));

        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)); // readable accents and Cyrillic

        services.AddOpenApi(options => options.AddDocumentTransformer<OpenApiDescription>());
    }

    // Before authentication: every request from one address shares one budget, so guessing keys
    // is slowed down before any hashing happens.
    private static RateLimitPartition<string> AddressPartition(HttpContext http)
    {
        var limits = http.RequestServices.GetRequiredService<IOptions<ThunderOptions>>().Value.RateLimit;
        return RateLimitPartition.GetFixedWindowLimiter("addr:" + http.Connection.RemoteIpAddress, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.PerAddressPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }

    // Fixed one-minute windows: per key once authenticated, per address before (which slows down key guessing).
    internal static RateLimitPartition<string> PartitionFor(HttpContext http)
    {
        var limits = http.RequestServices.GetRequiredService<IOptions<ThunderOptions>>().Value.RateLimit;
        var keyId = http.User.FindFirst(ApiKeyAuthentication.KeyIdClaim)?.Value;
        var (partition, permits) = keyId is not null
            ? ("key:" + keyId, limits.PerKeyPerMinute)
            : ("ip:" + http.Connection.RemoteIpAddress, limits.AnonymousPerMinute);

        return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }

    // Opens the folder and the journal now, so a bad setting stops the service at startup with a
    // clear message instead of failing the first request.
    private static void StartUp(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ThunderOptions>>().Value; // runs the validator
        var store = app.Services.GetRequiredService<TableStore>();
        var journal = app.Services.GetRequiredService<WriteJournal>();
        journal.EnsureWritable();
        _ = app.Services.GetRequiredService<AccessRegistry>();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Thunder.Dbf.Server");
        var writable = store.TableNames.Where(options.TableAcceptsWrites).ToList();
        logger.LogInformation("Serving {Folder}: {Count} tables, {Mode}. {Keys} API key(s).",
            store.Folder, store.TableNames.Count,
            writable.Count == 0 ? "read-only" : $"writes allowed to {string.Join(", ", writable)}",
            options.ApiKeys.Count);
        logger.LogInformation("Write journal: {Journal}. Backups: {Backups}.", journal.Path, store.BackupFolder);

        if (IsInside(journal.Path, store.Folder) || IsInside(store.BackupFolder, store.Folder))
            logger.LogWarning("The journal or backup folder is inside the data folder. Keep them outside it, so the legacy app's own backups and cleanups don't touch them.");
        if (options.ApiKeys.Count == 0)
            logger.LogWarning("No API keys are configured, so every request to /tables will get 401. Run 'thunder-dbf-server hash-key' to make one.");
    }

    private static bool IsInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}

/// <summary>The per-key (or, without a valid key, per-address) limit, applied once the key is known.</summary>
internal sealed class PerKeyRateLimit(RequestDelegate next, PartitionedRateLimiter<HttpContext> limiter)
{
    public async Task InvokeAsync(HttpContext http)
    {
        using var lease = await limiter.AcquireAsync(http, 1, http.RequestAborted);
        if (!lease.IsAcquired)
        {
            var keyed = http.User.FindFirst(ApiKeyAuthentication.KeyIdClaim) is not null;
            await RejectAsync(http, lease, keyed ? "This key has used its requests for the minute." : "Too many requests without a valid key from this address.");
            return;
        }
        await next(http);
    }

    public static async ValueTask RejectAsync(HttpContext http, RateLimitLease lease, string detail)
    {
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = Problems.Create(StatusCodes.Status429TooManyRequests, "Too many requests.", detail, "RateLimited",
                "Wait for the Retry-After seconds, then try again."),
        });
    }
}
