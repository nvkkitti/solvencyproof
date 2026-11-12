using System.Diagnostics;
using System.Threading.RateLimiting;
using SolvencyProof.Components;
using SolvencyProof.Helpers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SolvencyProof.Models;
using SolvencyProof.Services;
using SolvencyProof.Services.Blockchain;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // The address written by SolvencyProofContract/scripts/deploy.ts overrides appsettings.json (A deploy script által írt cím felülírja az appsettings.json-t)
        builder.Configuration.AddJsonFile(
            Path.Combine(builder.Environment.ContentRootPath, "..", "SolvencyProofContract", "deployments", "localhost.json"),
            optional: true,
            reloadOnChange: false);
        // Serilog reads its sinks and levels from the 'Serilog' section of appsettings.json
        builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services));
        var connectionString = builder.Configuration.GetConnectionString("SolvencyProofContext") ?? throw new InvalidOperationException("Connection string 'SolvencyProofContext' not found.");

        builder.Services.AddDbContextFactory<SolvencyProofContext>(options => options.UseSqlServer(connectionString));
        builder.Services.AddScoped<IAuditProverService, AuditProverService>();
        builder.Services.AddDataProtection();
        builder.Services.AddHttpClient();

        builder.Services.AddQuickGridEntityFrameworkAdapter();

        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddHttpClient<ZkProofClient>();
        builder.Services.AddSingleton<HardhatProofSubmitterService>();
        builder.Services.AddScoped<AccountInclusionVerifier>();

        // Per-IP limits for the public HTTP API (brute-force and abuse protection)
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy("public-status", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            options.AddPolicy("audit-generate", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });


        var app = builder.Build();

        app.UseSerilogRequestLogging();

        // Apply the migrations belonging to SolvencyProofContext to the configured database.
        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SolvencyProofContext>();
            dbContext.Database.Migrate();
        }

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }
        else
        {
            // Database schema pages expose internals; they are available only in development
            app.UseMigrationsEndPoint();
        }
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();

        app.UseRateLimiter();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.MapPost("/api/audit/generate", async (SolvencyProofContext db, IAuditProverService proverService) =>
        {
            var audit = new SolvencyAudit
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                Status = AuditStatus.Prepared
            };
            db.SolvencyAudit.Add(audit);
            await db.SaveChangesAsync();

            // Delegate generation to the background service (A generálás átadása a háttérszolgáltatásnak)
            // The service marks the audit as Failed and logs on error; this only observes the exception (A hibát a szolgáltatás naplózza)
            _ = proverService.GenerateProofAsync(audit.Id)
                .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            return Results.Accepted($"/api/audit/status/{audit.Id}");
        }).RequireRateLimiting("audit-generate");

        // GET: /api/audit/status/{id}
        app.MapGet("/api/audit/status/{id:guid}", async (Guid id, SolvencyProofContext db) =>
        {
            // Retrieve the audit record (Lekérdezzük az Audit rekordot (a CryptographicSecrets NÉLKÜL))
            var audit = await db.SolvencyAudit.FindAsync(id);

            if (audit == null)
            {
                return Results.NotFound(new { Error = "A kért audit nem található." });
            }

            // If the audit is still in progress (Ha az audit még folyamatban van (Pending vagy GeneratingProof))
            if (audit.Status != AuditStatus.VerifiedOnChain)
            {
                return Results.Ok(new
                {
                    Id = audit.Id,
                    Status = audit.Status.ToString(),
                    Timestamp = audit.Timestamp,
                    Message = "Az audit feldolgozása folyamatban van. Kérlek, próbáld újra később."
                });
            }

            // If the audit completed successfully, return the public proof: only public data, never the assets or the liabilities
            // (Ha az audit sikeres, a publikus bizonyítékot adjuk vissza: csak nyilvános adatot, az eszközöket és kötelezettségeket soha)
            return Results.Ok(new
            {
                Id = audit.Id,
                Status = audit.Status.ToString(),
                Timestamp = audit.Timestamp,
                TreeDepth = audit.TreeDepth,

                // Convert cryptographic byte arrays to hexadecimal strings (Kriptográfiai bájttömbök konvertálása Hexadecimális stringgé)
                MerkleRoot = Convert.ToHexString(audit.MerkleRoot).ToLower(),
                AssetsCommitment = Convert.ToHexString(audit.CommitmentCv).ToLower(),
                ZkProof = Convert.ToHexString(audit.ZkProof).ToLower()
            });
        })
        .RequireRateLimiting("public-status")
        .AddEndpointFilter(async (context, next) =>
        {
            // Pad every status response to the same minimum duration so timing does not reveal whether an id exists
            var stopwatch = Stopwatch.StartNew();
            object? result = await next(context);
            await ResponseTiming.EnsureMinimumAsync(stopwatch, ResponseTiming.PublicLookupMinimum);
            return result;
        });

        try
        {
            app.Run();
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}