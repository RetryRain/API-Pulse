using WebApplication1.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Short-lived in-memory caching of Application Insights query results, so repeat
// navigations and double-clicks don't re-run expensive Azure Monitor queries.
builder.Services.AddMemoryCache();

// Lightweight liveness/readiness endpoint for deployment health probes.
builder.Services.AddHealthChecks();

// API analyzer: bind Application Insights options and register the analytics service.
builder.Services.Configure<ApplicationInsightsOptions>(
    builder.Configuration.GetSection(ApplicationInsightsOptions.SectionName));
builder.Services.AddSingleton<IApiAnalyticsService, ApiAnalyticsService>();

// AI exception analysis (GitHub Models / OpenAI-compatible).
builder.Services.Configure<AiOptions>(
    builder.Configuration.GetSection(AiOptions.SectionName));
builder.Services.AddHttpClient<IExceptionAnalyzer, GitHubModelsExceptionAnalyzer>();

// API security scanner: probes a live endpoint and audits its HTTP security posture.
builder.Services.AddHttpClient<ISecurityScanner, SecurityScanner>();
builder.Services.AddHttpClient<ISecurityAdvisor, GitHubModelsSecurityAdvisor>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/ApiAnalyzer/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapControllerRoute(
        name: "default",
        pattern: "{controller=ApiAnalyzer}/{action=Index}/{id?}")
   .WithStaticAssets();

app.MapHealthChecks("/health");

app.Run();
