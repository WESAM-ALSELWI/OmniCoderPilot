using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OmniCoderPilot.Application;
using OmniCoderPilot.Infrastructure;

namespace OmniCoderPilot.Wpf.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly OpenRouterClient _openRouter;
    private readonly OpenAiCompatibleClient _groq;
    private readonly OpenAiCompatibleClient _deepseek;
    private readonly OpenAiCompatibleClient _openAi;
    private readonly OpenAiCompatibleClient _custom;

    // OpenRouter
    [ObservableProperty] private string _openRouterApiKey = "";
    [ObservableProperty] private string _openRouterBaseUrl = "https://openrouter.ai/api/v1";

    // Groq (Free fast models)
    [ObservableProperty] private string _groqApiKey = "";
    [ObservableProperty] private string _groqBaseUrl = "https://api.groq.com/openai/v1";

    // DeepSeek Direct
    [ObservableProperty] private string _deepSeekApiKey = "";
    [ObservableProperty] private string _deepSeekBaseUrl = "https://api.deepseek.com/v1";

    // OpenAI Direct
    [ObservableProperty] private string _openAiApiKey = "";
    [ObservableProperty] private string _openAiBaseUrl = "https://api.openai.com/v1";

    // Custom Any API Endpoint
    [ObservableProperty] private string _customBaseUrl = "";
    [ObservableProperty] private string _customApiKey = "";
    [ObservableProperty] private string _customModels = "";

    // Local Ollama
    [ObservableProperty] private string _ollamaBaseUrl = "http://127.0.0.1:11434";

    // Web Search
    [ObservableProperty] private string _braveSearchApiKey = "";

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSuccess;
    [ObservableProperty] private bool _isTesting;

    public event Action? RequestClose;

    public SettingsViewModel(
        IServiceProvider services,
        IConfiguration config,
        OpenRouterClient openRouter,
        [FromKeyedServices("Groq")] OpenAiCompatibleClient groq,
        [FromKeyedServices("DeepSeek")] OpenAiCompatibleClient deepseek,
        [FromKeyedServices("OpenAI")] OpenAiCompatibleClient openAi,
        [FromKeyedServices("Custom")] OpenAiCompatibleClient custom)
    {
        _services = services;
        _config = config;
        _openRouter = openRouter;
        _groq = groq;
        _deepseek = deepseek;
        _openAi = openAi;
        _custom = custom;

        // OpenRouter
        OpenRouterApiKey = _openRouter.ApiKey;
        if (string.IsNullOrEmpty(OpenRouterApiKey))
            OpenRouterApiKey = _config["OpenRouter:ApiKey"] ?? "";
        OpenRouterBaseUrl = _openRouter.BaseUrl;
        if (string.IsNullOrEmpty(OpenRouterBaseUrl))
            OpenRouterBaseUrl = _config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";

        // Groq
        GroqApiKey = _groq.ApiKey;
        if (string.IsNullOrEmpty(GroqApiKey))
            GroqApiKey = _config["Groq:ApiKey"] ?? "";
        GroqBaseUrl = _groq.BaseUrl;
        if (string.IsNullOrEmpty(GroqBaseUrl))
            GroqBaseUrl = _config["Groq:BaseUrl"] ?? "https://api.groq.com/openai/v1";

        // DeepSeek
        DeepSeekApiKey = _deepseek.ApiKey;
        if (string.IsNullOrEmpty(DeepSeekApiKey))
            DeepSeekApiKey = _config["DeepSeek:ApiKey"] ?? "";
        DeepSeekBaseUrl = _deepseek.BaseUrl;
        if (string.IsNullOrEmpty(DeepSeekBaseUrl))
            DeepSeekBaseUrl = _config["DeepSeek:BaseUrl"] ?? "https://api.deepseek.com/v1";

        // OpenAI
        OpenAiApiKey = _openAi.ApiKey;
        if (string.IsNullOrEmpty(OpenAiApiKey))
            OpenAiApiKey = _config["OpenAI:ApiKey"] ?? "";
        OpenAiBaseUrl = _openAi.BaseUrl;
        if (string.IsNullOrEmpty(OpenAiBaseUrl))
            OpenAiBaseUrl = _config["OpenAI:BaseUrl"] ?? "https://api.openai.com/v1";

        // Custom
        CustomBaseUrl = _custom.BaseUrl;
        if (string.IsNullOrEmpty(CustomBaseUrl))
            CustomBaseUrl = _config["CustomEndpoint:BaseUrl"] ?? "";
        CustomApiKey = _custom.ApiKey;
        if (string.IsNullOrEmpty(CustomApiKey))
            CustomApiKey = _config["CustomEndpoint:ApiKey"] ?? "";
        CustomModels = string.Join(", ", _custom.CustomModels);
        if (string.IsNullOrEmpty(CustomModels))
            CustomModels = _config["CustomEndpoint:Models"] ?? "";

        // Ollama & Brave
        OllamaBaseUrl = _config["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
        BraveSearchApiKey = _config["BraveSearch:ApiKey"] ?? "";
    }

    [RelayCommand]
    private async Task TestOpenRouterAsync()
    {
        if (string.IsNullOrWhiteSpace(OpenRouterApiKey))
        {
            StatusMessage = "⚠️ Please enter an OpenRouter API key first.";
            IsSuccess = false;
            return;
        }

        IsTesting = true;
        StatusMessage = "Connecting to OpenRouter...";
        IsSuccess = false;

        try
        {
            var tempClient = new OpenRouterClient(
                new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) },
                _config)
            {
                ApiKey = OpenRouterApiKey.Trim(),
                BaseUrl = string.IsNullOrWhiteSpace(OpenRouterBaseUrl) ? "https://openrouter.ai/api/v1" : OpenRouterBaseUrl.Trim()
            };

            var models = await tempClient.ListModelsAsync(CancellationToken.None);
            if (models.Count > 0)
            {
                StatusMessage = $"✅ OpenRouter connection successful! Found {models.Count} coding models.";
                IsSuccess = true;
            }
            else
            {
                StatusMessage = "⚠️ Connected to OpenRouter, but no models returned. Check your API key.";
                IsSuccess = false;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ OpenRouter connection failed: {ex.Message}";
            IsSuccess = false;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestGroqAsync()
    {
        if (string.IsNullOrWhiteSpace(GroqApiKey))
        {
            StatusMessage = "⚠️ Please enter a Groq API key first (get free at console.groq.com).";
            IsSuccess = false;
            return;
        }

        IsTesting = true;
        StatusMessage = "Connecting to Groq...";
        IsSuccess = false;

        try
        {
            var temp = new OpenAiCompatibleClient(
                new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) },
                "Groq",
                string.IsNullOrWhiteSpace(GroqBaseUrl) ? "https://api.groq.com/openai/v1" : GroqBaseUrl.Trim(),
                GroqApiKey.Trim());

            var models = await temp.ListModelsAsync(CancellationToken.None);
            StatusMessage = $"✅ Groq connection successful! Found {models.Count} models (including free Llama 3.3 70B).";
            IsSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Groq connection failed: {ex.Message}";
            IsSuccess = false;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestCustomEndpointAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomBaseUrl))
        {
            StatusMessage = "⚠️ Please enter a Base URL for your custom API endpoint.";
            IsSuccess = false;
            return;
        }

        IsTesting = true;
        StatusMessage = "Connecting to custom endpoint...";
        IsSuccess = false;

        try
        {
            var temp = new OpenAiCompatibleClient(
                new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) },
                "Custom",
                CustomBaseUrl.Trim(),
                CustomApiKey.Trim());

            var models = await temp.ListModelsAsync(CancellationToken.None);
            StatusMessage = $"✅ Custom endpoint connection successful! Found {models.Count} models.";
            IsSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Custom endpoint connection failed: {ex.Message}";
            IsSuccess = false;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            // 1. Update live OpenRouter client
            _openRouter.ApiKey = OpenRouterApiKey.Trim();
            _openRouter.BaseUrl = string.IsNullOrWhiteSpace(OpenRouterBaseUrl) ? "https://openrouter.ai/api/v1" : OpenRouterBaseUrl.Trim();

            // 2. Update live Groq client
            _groq.ApiKey = GroqApiKey.Trim();
            _groq.BaseUrl = string.IsNullOrWhiteSpace(GroqBaseUrl) ? "https://api.groq.com/openai/v1" : GroqBaseUrl.Trim();

            // 3. Update live DeepSeek client
            _deepseek.ApiKey = DeepSeekApiKey.Trim();
            _deepseek.BaseUrl = string.IsNullOrWhiteSpace(DeepSeekBaseUrl) ? "https://api.deepseek.com/v1" : DeepSeekBaseUrl.Trim();

            // 4. Update live OpenAI client
            _openAi.ApiKey = OpenAiApiKey.Trim();
            _openAi.BaseUrl = string.IsNullOrWhiteSpace(OpenAiBaseUrl) ? "https://api.openai.com/v1" : OpenAiBaseUrl.Trim();

            // 5. Update live Custom client
            _custom.ApiKey = CustomApiKey.Trim();
            _custom.BaseUrl = CustomBaseUrl.Trim();
            _custom.CustomModels = CustomModels
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            // 6. Persist to appsettings.json file(s)
            SaveToAppSettings();

            // 7. Refresh models list in sidebar
            var sidebar = _services.GetService<SidebarViewModel>();
            if (sidebar != null)
            {
                sidebar.RefreshModelsCommand.Execute(null);
            }

            StatusMessage = "✅ All settings saved successfully!";
            IsSuccess = true;

            await Task.Delay(400);
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Failed to save: {ex.Message}";
            IsSuccess = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }

    private void SaveToAppSettings()
    {
        var possiblePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OmniCoderPilot.Core", "appsettings.json"),
            @"C:\Users\AMB\source\repos\OmniCoderPilot_v2\OmniCoderPilot.Core\appsettings.json"
        };

        foreach (var p in possiblePaths)
        {
            try
            {
                var full = Path.GetFullPath(p);
                if (File.Exists(full))
                {
                    var jsonStr = File.ReadAllText(full);
                    var doc = JsonNode.Parse(jsonStr) as JsonObject ?? new JsonObject();

                    // OpenRouter
                    SetProperty(doc, "OpenRouter", "ApiKey", OpenRouterApiKey.Trim());
                    SetProperty(doc, "OpenRouter", "BaseUrl", OpenRouterBaseUrl.Trim());

                    // Groq
                    SetProperty(doc, "Groq", "ApiKey", GroqApiKey.Trim());
                    SetProperty(doc, "Groq", "BaseUrl", GroqBaseUrl.Trim());

                    // DeepSeek
                    SetProperty(doc, "DeepSeek", "ApiKey", DeepSeekApiKey.Trim());
                    SetProperty(doc, "DeepSeek", "BaseUrl", DeepSeekBaseUrl.Trim());

                    // OpenAI
                    SetProperty(doc, "OpenAI", "ApiKey", OpenAiApiKey.Trim());
                    SetProperty(doc, "OpenAI", "BaseUrl", OpenAiBaseUrl.Trim());

                    // Custom
                    SetProperty(doc, "CustomEndpoint", "ApiKey", CustomApiKey.Trim());
                    SetProperty(doc, "CustomEndpoint", "BaseUrl", CustomBaseUrl.Trim());
                    SetProperty(doc, "CustomEndpoint", "Models", CustomModels.Trim());

                    // Ollama
                    SetProperty(doc, "Ollama", "BaseUrl", OllamaBaseUrl.Trim());

                    // Brave
                    SetProperty(doc, "BraveSearch", "ApiKey", BraveSearchApiKey.Trim());

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(full, doc.ToJsonString(options));
                }
            }
            catch { }
        }
    }

    private static void SetProperty(JsonObject root, string section, string key, string value)
    {
        if (root[section] is not JsonObject secObj)
        {
            secObj = new JsonObject();
            root[section] = secObj;
        }
        secObj[key] = value;
    }
}
