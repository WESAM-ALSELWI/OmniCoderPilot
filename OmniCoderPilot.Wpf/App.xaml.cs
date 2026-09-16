using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OmniCoderPilot.Application;
using OmniCoderPilot.Application.Tools;
using OmniCoderPilot.Infrastructure;
using OmniCoderPilot.Wpf.Services;
using OmniCoderPilot.Wpf.ViewModels;

namespace OmniCoderPilot.Wpf;

public partial class App : System.Windows.Application
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OmniCoderPilot.Core", "appsettings.json"), optional: true)
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services, config);
        ServiceProvider = services.BuildServiceProvider();

        // Ensure database is created
        {
            var factory = ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            await SqliteSchemaInitializer.EnsureCompatibleSchemaAsync(db);
        }

        // Set main window DataContext
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.DataContext = ServiceProvider.GetRequiredService<MainViewModel>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // ── WPF Infrastructure ────────────────────────────────────────────
        services.AddSingleton(Dispatcher.CurrentDispatcher);
        services.AddSingleton<IEventAggregator, EventAggregator>();
        services.AddSingleton<IAgentEventSink, WpfEventSink>();

        // ── Core Infrastructure ───────────────────────────────────────────
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContextFactory<AppDbContext>(opts =>
            opts.UseSqlite(config.GetConnectionString("OmniCoderPilot") ?? "Data Source=mycoder.db"));

        // ── LLM Clients: local Ollama + Cloud Providers ──────────────────
        services.AddHttpClient("Ollama", client =>
        {
            var baseUrl = config["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        });

        services.AddHttpClient("OpenRouter", client =>
        {
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        });

        services.AddSingleton<OllamaClient>(sp =>
        {
            var factory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = factory.CreateClient("Ollama");
            return new OllamaClient(http, config);
        });

        services.AddSingleton<OpenRouterClient>(sp =>
        {
            var factory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = factory.CreateClient("OpenRouter");
            return new OpenRouterClient(http, config);
        });

        // Groq Direct (Free fast cloud models)
        services.AddKeyedSingleton<OpenAiCompatibleClient>("Groq", (sp, key) =>
        {
            var http = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("Groq");
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            var apiKey = config["Groq:ApiKey"] ?? "";
            var baseUrl = config["Groq:BaseUrl"] ?? "https://api.groq.com/openai/v1";
            var client = new OpenAiCompatibleClient(http, "Groq", baseUrl, apiKey);
            client.CustomModels.AddRange(["llama-3.3-70b-versatile", "deepseek-r1-distill-llama-70b", "qwen-2.5-coder-32b"]);
            return client;
        });

        // DeepSeek Official Direct
        services.AddKeyedSingleton<OpenAiCompatibleClient>("DeepSeek", (sp, key) =>
        {
            var http = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("DeepSeek");
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            var apiKey = config["DeepSeek:ApiKey"] ?? "";
            var baseUrl = config["DeepSeek:BaseUrl"] ?? "https://api.deepseek.com/v1";
            var client = new OpenAiCompatibleClient(http, "DeepSeek", baseUrl, apiKey);
            client.CustomModels.AddRange(["deepseek-chat", "deepseek-reasoner"]);
            return client;
        });

        // OpenAI Official Direct
        services.AddKeyedSingleton<OpenAiCompatibleClient>("OpenAI", (sp, key) =>
        {
            var http = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("OpenAI");
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            var apiKey = config["OpenAI:ApiKey"] ?? "";
            var baseUrl = config["OpenAI:BaseUrl"] ?? "https://api.openai.com/v1";
            var client = new OpenAiCompatibleClient(http, "OpenAI", baseUrl, apiKey);
            client.CustomModels.AddRange(["gpt-4o", "gpt-4o-mini", "o3-mini", "o1"]);
            return client;
        });

        // Custom Any Endpoint
        services.AddKeyedSingleton<OpenAiCompatibleClient>("Custom", (sp, key) =>
        {
            var http = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("Custom");
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            var apiKey = config["CustomEndpoint:ApiKey"] ?? "";
            var baseUrl = config["CustomEndpoint:BaseUrl"] ?? "";
            var modelsStr = config["CustomEndpoint:Models"] ?? "";
            var client = new OpenAiCompatibleClient(http, "Custom", baseUrl, apiKey);
            if (!string.IsNullOrWhiteSpace(modelsStr))
                client.CustomModels.AddRange(modelsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return client;
        });

        // ModelRouter is the primary IOllamaClient — routes local vs cloud providers by model prefix
        services.AddSingleton<IOllamaClient, ModelRouter>(sp =>
            new ModelRouter(
                sp.GetRequiredService<OllamaClient>(),
                sp.GetRequiredService<OpenRouterClient>(),
                sp.GetRequiredKeyedService<OpenAiCompatibleClient>("Groq"),
                sp.GetRequiredKeyedService<OpenAiCompatibleClient>("DeepSeek"),
                sp.GetRequiredKeyedService<OpenAiCompatibleClient>("OpenAI"),
                sp.GetRequiredKeyedService<OpenAiCompatibleClient>("Custom")
            ));

        // ── Web Services ──────────────────────────────────────────────────
        services.AddHttpClient<IWebFetchService, WebFetchService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<WebSearchService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<IWebSearchService>(sp => sp.GetRequiredService<WebSearchService>());

        services.AddHttpClient<NuGetSearchService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<INuGetSearchService>(sp => sp.GetRequiredService<NuGetSearchService>());

        // ── Core Services ─────────────────────────────────────────────────
        services.AddSingleton<IWorkspaceFileService, WorkspaceFileService>();
        services.AddSingleton<IRepositoryIndexer, RepositoryIndexer>();
        services.AddSingleton<IDiffService, DiffService>();
        services.AddSingleton<ITerminalService, TerminalService>();
        services.AddSingleton<IPersistentShellFactory, PersistentShellFactory>();
        services.AddSingleton<IProjectUnderstandingService, ProjectUnderstandingService>();
        services.AddSingleton<IProjectMemoryService, ProjectMemoryService>();
        services.AddSingleton<IMemoryService, MemoryService>();
        services.AddSingleton<IContextCompressionService, ContextCompressionService>();
        services.AddSingleton<ITodoService, TodoService>();

        // ── Phase 3-8: New infrastructure ────────────────────────────────
        services.AddSingleton<IContextCompactor, ContextCompactor>();
        services.AddSingleton<ISubAgentManager, SubAgentManager>();
        services.AddSingleton<ISkillLoader, SkillLoader>(sp =>
            new SkillLoader(Environment.CurrentDirectory));

        services.AddSingleton<PlanModeState>();

        // ── Agent Tools ───────────────────────────────────────────────────
        // File tools
        services.AddSingleton<IAgentTool, ReadFileTool>();
        services.AddSingleton<IAgentTool, WriteFileTool>();
        services.AddSingleton<IAgentTool, AppendFileTool>();
        services.AddSingleton<IAgentTool, EditFileTool>();
        services.AddSingleton<IAgentTool, DeleteFileTool>();
        services.AddSingleton<IAgentTool, GetFileInfoTool>();
        services.AddSingleton<IAgentTool, RenameFileTool>();

        // Search tools
        services.AddSingleton<IAgentTool, SearchFilesTool>();
        services.AddSingleton<IAgentTool, GlobTool>();
        services.AddSingleton<IAgentTool, SearchTextTool>();
        services.AddSingleton<IAgentTool, GrepTool>();

        // Code intelligence tools (NEW)
        services.AddSingleton<IAgentTool, CodeSearchTool>();
        services.AddSingleton<IAgentTool, SymbolSearchTool>();
        services.AddSingleton<IAgentTool, IndexWorkspaceTool>();

        // Terminal tools
        services.AddSingleton<IAgentTool, ExecuteCommandTool>();
        services.AddSingleton<IAgentTool, PersistentShellTool>(); // NEW

        // Directory tools
        services.AddSingleton<IAgentTool, ListDirectoryTool>();
        services.AddSingleton<IAgentTool, CreateDirectoryTool>();

        // Git tools (NEW)
        services.AddSingleton<IAgentTool, GitStatusTool>();
        services.AddSingleton<IAgentTool, GitDiffTool>();
        services.AddSingleton<IAgentTool, GitLogTool>();
        services.AddSingleton<IAgentTool, GitBlameTool>();
        services.AddSingleton<IAgentTool, GitCommitTool>();
        services.AddSingleton<IAgentTool, GitBranchTool>();
        services.AddSingleton<IAgentTool, GitStashTool>();

        // Web tools
        services.AddSingleton<IAgentTool, WebFetchTool>();
        services.AddSingleton<IAgentTool, WebSearchTool>();
        services.AddSingleton<IAgentTool, NuGetSearchTool>(); // NEW

        // Test tools (NEW)
        services.AddSingleton<IAgentTool, RunTestsTool>();
        services.AddSingleton<IAgentTool, RunSpecificTestTool>();

        // Task / plan tools
        services.AddSingleton<IAgentTool, TodoWriteTool>();
        services.AddSingleton<IAgentTool, TodoReadTool>();
        services.AddSingleton<IAgentTool, EnterPlanModeTool>();
        services.AddSingleton<IAgentTool, ExitPlanModeTool>();

        // Document tools
        services.AddSingleton<IAgentTool, CreateWordDocumentTool>();
        services.AddSingleton<IAgentTool, CreateExcelDocumentTool>();
        services.AddSingleton<IAgentTool, CreatePowerPointDocumentTool>();

        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();

        // ── WPF ViewModels ────────────────────────────────────────────────
        services.AddTransient<MainViewModel>();
        services.AddTransient<SidebarViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddTransient<ActivityViewModel>();
        services.AddTransient<ToolCardsViewModel>();
        services.AddTransient<TodoViewModel>();
        services.AddTransient<FileTreeViewModel>();
        services.AddTransient<DiffViewModel>();
        services.AddTransient<FolderBrowserViewModel>();
        services.AddTransient<SettingsViewModel>();

        // ── WPF Views ─────────────────────────────────────────────────────
        services.AddTransient<MainWindow>();
        services.AddTransient<Views.SettingsDialog>();
    }
}
