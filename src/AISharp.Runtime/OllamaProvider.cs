namespace AISharp.Runtime;

// ─── Ollama provider configuration ───────────────────────────────────────────

/// <summary>
/// Configuration for the Ollama local model provider.
/// All fields have safe defaults; only <see cref="Model"/> is required.
/// </summary>
public sealed class OllamaProviderConfig
{
    /// <summary>Ollama HTTP API base URL. Defaults to <c>http://localhost:11434</c>.</summary>
    public Uri Endpoint { get; init; } = new Uri("http://localhost:11434");

    /// <summary>Model name to use (e.g. <c>llama3</c>, <c>mistral</c>).</summary>
    public string Model { get; init; } = "llama3";

    /// <summary>Request timeout. Defaults to 120 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Optional AI# policy name controlling this local provider.
    /// Local-only policies allow Ollama because inference runs on the configured endpoint.
    /// </summary>
    public string? PolicyName { get; init; }

    /// <summary>
    /// When true, requests include a <c>format: "json"</c> constraint so Ollama
    /// returns well-formed JSON (model-dependent support; defaults to true).
    /// </summary>
    public bool RequestJsonFormat { get; init; } = true;
}

// ─── Ollama HTTP response shapes ──────────────────────────────────────────────

internal sealed class OllamaGenerateResponse
{
    public string? Response { get; set; }
    public bool Done { get; set; }
    public string? Error { get; set; }
}

internal sealed class OllamaGenerateRequest
{
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public bool Stream { get; set; }
    public string? Format { get; set; }
    public OllamaOptions? Options { get; set; }
}

internal sealed class OllamaOptions
{
    public long? Seed { get; set; }
    public double? Temperature { get; set; }
}

// ─── Ollama model runtime ─────────────────────────────────────────────────────

/// <summary>
/// <see cref="IAISharpModelRuntime"/> implementation that drives a local Ollama server
/// via its HTTP API.
/// </summary>
/// <remarks>
/// Tests inject a fake <see cref="HttpMessageHandler"/> to avoid requiring a live server.
/// </remarks>
public sealed class OllamaModelRuntime : IAISharpModelRuntime, IDisposable
{
    private readonly OllamaProviderConfig _config;
    private readonly PolicyRuntimeContext _policy;
    private readonly HttpClient _http;
    private bool _disposed;

    /// <summary>
    /// Creates a new runtime with the supplied configuration.
    /// An optional <paramref name="httpHandler"/> replaces the default handler —
    /// use a fake for tests.
    /// </summary>
    public OllamaModelRuntime(
        OllamaProviderConfig config,
        PolicyRuntimeContext? policy = null,
        HttpMessageHandler? httpHandler = null)
    {
        _config = config;
        _policy = policy ?? new PolicyRuntimeContext();
        _http = httpHandler is not null
            ? new HttpClient(httpHandler) { BaseAddress = config.Endpoint, Timeout = config.Timeout }
            : new HttpClient { BaseAddress = config.Endpoint, Timeout = config.Timeout };
    }

    /// <inheritdoc/>
    public ModelProviderKind ProviderKind => ModelProviderKind.Local;

    /// <inheritdoc/>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("/", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    /// <exception cref="PolicyViolationException">
    /// Thrown if the request's policy does not permit the configured provider.
    /// </exception>
    public async Task<ModelResponse> ExecuteAsync(ModelRequest request)
    {
        if (request.SchemaName is null)
            throw new ArgumentException(
                $"AI# model call to Ollama model '{_config.Model}' is missing required output schema.",
                nameof(request));

        if (_config.PolicyName is not null)
            _ = _policy.GetPolicy(_config.PolicyName);

        var prompt = BuildPrompt(request);

        var ollamaReq = new OllamaGenerateRequest
        {
            Model = _config.Model,
            Prompt = prompt,
            Stream = false,
            Format = _config.RequestJsonFormat ? "json" : null,
            Options = request.Replay is { } r
                ? new OllamaOptions { Seed = r.Seed, Temperature = r.Temperature }
                : null
        };

        string json = System.Text.Json.JsonSerializer.Serialize(ollamaReq, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        using var cts = request.Cancellation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation)
            : new CancellationTokenSource();

        StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _http.PostAsync("/api/generate", content, cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new ModelResponse(false, null,
                BuildUnavailableDiagnostic(ex), null, null);
        }

        string responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
            return new ModelResponse(false, null,
                $"Ollama model '{_config.Model}' at '{_config.Endpoint}' returned HTTP {(int)httpResponse.StatusCode}: {responseBody}",
                null,
                null);

        OllamaGenerateResponse? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<OllamaGenerateResponse>(
                responseBody,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            return new ModelResponse(false, null,
                $"Failed to parse Ollama response: {ex.Message}", null, null);
        }

        if (parsed is null || !string.IsNullOrEmpty(parsed.Error))
            return new ModelResponse(false, null,
                $"Ollama error: {parsed?.Error ?? "empty response"}", null, null);

        var output = parsed.Response ?? "";
        var hash = ComputeHash(output);

        return new ModelResponse(true, output, null, hash, null);
    }

    private static string BuildPrompt(ModelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Task))
            return request.InputPayload;

        return $"Task: {request.Task}\n\nInput:\n{request.InputPayload}";
    }

    private static string ComputeHash(string output)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(output);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string BuildUnavailableDiagnostic(Exception ex) =>
        $"Ollama request failed: model '{_config.Model}' is unavailable at '{_config.Endpoint}'. " +
        "Start Ollama with 'ollama serve', verify the configured endpoint and model, then retry. " +
        $"Underlying error: {ex.Message}";

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _http.Dispose();
            _disposed = true;
        }
    }
}
