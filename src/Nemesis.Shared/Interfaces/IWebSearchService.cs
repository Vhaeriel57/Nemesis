using Nemesis.Shared.DTOs;

namespace Nemesis.Shared.Interfaces;

public interface IWebSearchService
{
    Task<WebSearchResult> SearchAsync(
        string query,
        int maxResults = 10,
        CancellationToken cancellationToken = default);

    Task<WebPageContent> FetchPageAsync(
        string url,
        bool useCache = true,
        CancellationToken cancellationToken = default);

    Task<List<WebPageContent>> FetchPagesAsync(
        List<string> urls,
        bool useCache = true,
        CancellationToken cancellationToken = default);

    Task ClearCacheAsync(CancellationToken cancellationToken = default);
    Task<long> GetCacheSizeAsync(CancellationToken cancellationToken = default);
}
