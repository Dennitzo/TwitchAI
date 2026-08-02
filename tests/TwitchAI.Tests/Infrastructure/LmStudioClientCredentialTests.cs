using System.Net;
using System.Net.Http.Headers;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;

namespace TwitchAI.Tests.Infrastructure;

public sealed class LmStudioClientCredentialTests
{
    [Fact]
    public async Task GetLoadedModels_WhenVaultReturnsToken_SendsBearerAuthorization()
    {
        var handler = new CapturingHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:1234/"),
        };
        var vault = new FakeLmStudioCredentialVault("local-secret-token");
        var metrics = new FakeLlmRunMetricsStore();
        var sut = new LmStudioClient(
            httpClient,
            metrics,
            vault,
            CreateOptions());

        await sut.GetLoadedModelsAsync();

        Assert.Equal(1, vault.LoadCalls);
        Assert.NotNull(handler.Authorization);
        Assert.Equal("Bearer", handler.Authorization.Scheme);
        Assert.Equal("local-secret-token", handler.Authorization.Parameter);
        Assert.Empty(metrics.Records);
    }

    [Fact]
    public async Task GetLoadedModels_WhenVaultReturnsNoToken_OmitsAuthorization()
    {
        var handler = new CapturingHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:1234/"),
        };
        var vault = new FakeLmStudioCredentialVault(token: null);
        var metrics = new FakeLlmRunMetricsStore();
        var sut = new LmStudioClient(
            httpClient,
            metrics,
            vault,
            CreateOptions());

        await sut.GetLoadedModelsAsync();

        Assert.Equal(1, vault.LoadCalls);
        Assert.Null(handler.Authorization);
        Assert.Empty(metrics.Records);
    }

    [Fact]
    public async Task GetLoadedModels_WhenBaseAddressChanges_UsesCurrentAddress()
    {
        var handler = new CapturingHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:1234/"),
        };
        var options = CreateOptions();
        var sut = new LmStudioClient(
            httpClient,
            new FakeLlmRunMetricsStore(),
            new FakeLmStudioCredentialVault(token: null),
            options);
        options.LmStudio.BaseAddress = new Uri("http://192.168.0.67:1234/");

        await sut.GetLoadedModelsAsync();

        Assert.Equal(
            new Uri("http://192.168.0.67:1234/v1/models"),
            handler.RequestUri);
    }

    [Fact]
    public async Task GetLoadedModels_UsesOpenAiCompatibleModelsEndpointAndSchema()
    {
        var handler = new CapturingHttpMessageHandler(
            """{"object":"list","data":[{"id":"openai/gpt-oss-20b","object":"model"}]}""");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:1234/"),
        };
        var sut = new LmStudioClient(
            httpClient,
            new FakeLlmRunMetricsStore(),
            new FakeLmStudioCredentialVault(token: null),
            CreateOptions());

        var models = await sut.GetLoadedModelsAsync();

        Assert.Equal(["openai/gpt-oss-20b"], models);
        Assert.Equal(
            new Uri("http://127.0.0.1:1234/v1/models"),
            handler.RequestUri);
    }

    private static InfrastructureOptions CreateOptions()
    {
        return new InfrastructureOptions
        {
            LmStudio = new LmStudioOptions
            {
                BaseAddress = new Uri("http://127.0.0.1:1234/"),
            },
        };
    }

    private sealed class CapturingHttpMessageHandler(
        string responseBody = """{"models":[]}""") : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization is { } authorization
                ? new AuthenticationHeaderValue(
                    authorization.Scheme,
                    authorization.Parameter)
                : null;

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody),
                });
        }
    }

    private sealed class FakeLmStudioCredentialVault(string? token)
        : ILmStudioCredentialVault
    {
        public int LoadCalls { get; private set; }

        public Task<string?> LoadTokenAsync(
            CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return Task.FromResult(token);
        }

        public Task SaveTokenAsync(
            string tokenValue,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLlmRunMetricsStore : ILlmRunMetricsStore
    {
        public List<LlmRunMetric> Records { get; } = [];

        public Task RecordAsync(
            LlmRunMetric metric,
            CancellationToken cancellationToken = default)
        {
            Records.Add(metric);
            return Task.CompletedTask;
        }
    }
}
