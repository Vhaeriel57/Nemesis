using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;

namespace Nemesis.AgentCore.Tools;

public class WebSearchTool : ITool, IWebSearchService
{
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private readonly int _cacheExpirationHours;
    private readonly List<string> _allowedDomains;

    public string Name => "web_search";
    public string Description => "Search the web for Unity documentation, forums, and programming resources. Respects domain whitelist for security.";

    public ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ToolParameter>
        {
            ["action"] = new()
            {
                Type = "string",
                Description = "The action to perform",
                Enum = new List<string> { "search", "fetch" }
            },
            ["query"] = new()
            {
                Type = "string",
                Description = "Search query or URL to fetch"
            },
            ["max_results"] = new()
            {
                Type = "integer",
                Description = "Maximum number of search results (default: 5)"
            }
        },
        Required = new List<string> { "action", "query" }
    };

    public WebSearchTool(WebSearchSettings? settings = null)
    {
        settings ??= new WebSearchSettings();
        _cachePath = settings.CachePath;
        _cacheExpirationHours = settings.CacheExpirationHours;
        _allowedDomains = settings.AllowedDomains;

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        Directory.CreateDirectory(_cachePath);
    }

    public async Task<ToolResult> ExecuteAsync(
        Dictionary<string, object> parameters,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.NetworkEnabled)
        {
            return new ToolResult
            {
                Success = false,
                Error = "Network access is disabled (offline mode)"
            };
        }

        var action = parameters.GetValueOrDefault("action")?.ToString() ?? "";
        var query = parameters.GetValueOrDefault("query")?.ToString() ?? "";
        var maxResults = 5;

        if (parameters.TryGetValue("max_results", out var maxObj) && int.TryParse(maxObj?.ToString(), out var parsed))
        {
            maxResults = Math.Min(parsed, 20);
        }

        return action.ToLower() switch
        {
            "search" => await SearchAsync(query, maxResults, cancellationToken),
            "fetch" => await FetchAsync(query, cancellationToken),
            _ => new ToolResult { Success = false, Error = $"Unknown action: {action}" }
        };
    }

    private async Task<ToolResult> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        try
        {
            var searchResult = await SearchAsync(query, maxResults, cancellationToken);

            var sb = new StringBuilder();
            sb.AppendLine($"Search results for: {query}");
            sb.AppendLine();

            foreach (var result in searchResult.Results)
            {
                sb.AppendLine($"### {result.Title}");
                sb.AppendLine($"URL: {result.Url}");
                sb.AppendLine(result.Snippet);
                sb.AppendLine();
            }

            return new ToolResult
            {
                Success = true,
                Output = sb.ToString(),
                Data = searchResult
            };
        }
        catch (Exception ex)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Search failed: {ex.Message}"
            };
        }
    }

    private async Task<ToolResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var content = await FetchPageAsync(url, true, cancellationToken);

            return new ToolResult
            {
                Success = true,
                Output = $"# {content.Title}\n\n{content.Content}",
                Data = content
            };
        }
        catch (Exception ex)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Fetch failed: {ex.Message}"
            };
        }
    }

    public async Task<WebSearchResult> SearchAsync(
        string query,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        // Check cache first
        var cacheKey = GetCacheKey($"search:{query}:{maxResults}");
        var cached = await GetFromCacheAsync<WebSearchResult>(cacheKey);
        if (cached != null)
        {
            cached.FromCache = true;
            return cached;
        }

        // Use DuckDuckGo HTML search (no API key required)
        var results = await SearchDuckDuckGoAsync(query, maxResults, cancellationToken);

        var searchResult = new WebSearchResult
        {
            Query = query,
            Results = results,
            SearchedAt = DateTime.UtcNow,
            FromCache = false
        };

        await SaveToCacheAsync(cacheKey, searchResult);
        return searchResult;
    }

    private async Task<List<SearchResultItem>> SearchDuckDuckGoAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var results = new List<SearchResultItem>();

        try
        {
            // Add site restrictions for allowed domains
            var siteQuery = string.Join(" OR ", _allowedDomains.Select(d => $"site:{d}"));
            var fullQuery = $"{query} ({siteQuery})";

            var encodedQuery = WebUtility.UrlEncode(fullQuery);
            var url = $"https://html.duckduckgo.com/html/?q={encodedQuery}";

            var response = await _httpClient.GetStringAsync(url, cancellationToken);

            var doc = new HtmlDocument();
            doc.LoadHtml(response);

            var resultNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'result')]");
            if (resultNodes == null)
                return results;

            foreach (var node in resultNodes.Take(maxResults))
            {
                var titleNode = node.SelectSingleNode(".//a[contains(@class, 'result__a')]");
                var snippetNode = node.SelectSingleNode(".//a[contains(@class, 'result__snippet')]");

                if (titleNode == null)
                    continue;

                var href = titleNode.GetAttributeValue("href", "");
                var actualUrl = ExtractUrlFromDdgRedirect(href);

                if (string.IsNullOrEmpty(actualUrl))
                    continue;

                results.Add(new SearchResultItem
                {
                    Title = WebUtility.HtmlDecode(titleNode.InnerText.Trim()),
                    Url = actualUrl,
                    Snippet = snippetNode != null
                        ? WebUtility.HtmlDecode(snippetNode.InnerText.Trim())
                        : ""
                });
            }
        }
        catch (Exception)
        {
            // Fall back to empty results on error
        }

        return results;
    }

    private string ExtractUrlFromDdgRedirect(string href)
    {
        if (string.IsNullOrEmpty(href))
            return "";

        // DuckDuckGo uses redirect URLs
        var match = Regex.Match(href, @"uddg=([^&]+)");
        if (match.Success)
        {
            return WebUtility.UrlDecode(match.Groups[1].Value);
        }

        if (href.StartsWith("http"))
            return href;

        return "";
    }

    public async Task<WebPageContent> FetchPageAsync(
        string url,
        bool useCache = true,
        CancellationToken cancellationToken = default)
    {
        // Validate URL against allowed domains
        if (!IsUrlAllowed(url))
        {
            throw new InvalidOperationException($"URL domain not in allowed list: {url}");
        }

        // Check cache
        if (useCache)
        {
            var cacheKey = GetCacheKey($"page:{url}");
            var cached = await GetFromCacheAsync<WebPageContent>(cacheKey);
            if (cached != null)
            {
                cached.FromCache = true;
                return cached;
            }
        }

        var response = await _httpClient.GetStringAsync(url, cancellationToken);

        var doc = new HtmlDocument();
        doc.LoadHtml(response);

        // Remove script and style elements
        var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header");
        if (nodesToRemove != null)
        {
            foreach (var node in nodesToRemove)
            {
                node.Remove();
            }
        }

        // Extract title
        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim() ?? "";

        // Extract main content
        var mainContent = doc.DocumentNode.SelectSingleNode("//main|//article|//div[@class='content']|//div[@id='content']")
            ?? doc.DocumentNode.SelectSingleNode("//body");

        var content = mainContent != null
            ? CleanHtmlContent(mainContent)
            : "";

        // Extract code blocks
        var codeBlocks = new List<string>();
        var codeNodes = doc.DocumentNode.SelectNodes("//pre|//code");
        if (codeNodes != null)
        {
            foreach (var codeNode in codeNodes)
            {
                var code = WebUtility.HtmlDecode(codeNode.InnerText.Trim());
                if (code.Length > 10)
                {
                    codeBlocks.Add(code);
                }
            }
        }

        var pageContent = new WebPageContent
        {
            Url = url,
            Title = WebUtility.HtmlDecode(title),
            Content = content,
            FetchedAt = DateTime.UtcNow,
            FromCache = false,
            CodeBlocks = codeBlocks.Take(20).ToList()
        };

        // Cache the result
        var key = GetCacheKey($"page:{url}");
        await SaveToCacheAsync(key, pageContent);

        return pageContent;
    }

    private string CleanHtmlContent(HtmlNode node)
    {
        var sb = new StringBuilder();
        CleanNodeRecursive(node, sb);
        var text = sb.ToString();

        // Clean up whitespace
        text = Regex.Replace(text, @"\n\s*\n\s*\n", "\n\n");
        text = Regex.Replace(text, @"[ \t]+", " ");

        // Limit length
        if (text.Length > 50000)
        {
            text = text.Substring(0, 50000) + "\n... [truncated]";
        }

        return text.Trim();
    }

    private void CleanNodeRecursive(HtmlNode node, StringBuilder sb)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = WebUtility.HtmlDecode(node.InnerText);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(text.Trim() + " ");
            }
            return;
        }

        if (node.Name == "br" || node.Name == "p" || node.Name == "div" ||
            node.Name == "h1" || node.Name == "h2" || node.Name == "h3" ||
            node.Name == "h4" || node.Name == "h5" || node.Name == "h6" ||
            node.Name == "li")
        {
            sb.AppendLine();
        }

        if (node.Name == "pre" || node.Name == "code")
        {
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(WebUtility.HtmlDecode(node.InnerText.Trim()));
            sb.AppendLine("```");
            sb.AppendLine();
            return;
        }

        foreach (var child in node.ChildNodes)
        {
            CleanNodeRecursive(child, sb);
        }
    }

    private bool IsUrlAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return _allowedDomains.Any(domain =>
            uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<WebPageContent>> FetchPagesAsync(
        List<string> urls,
        bool useCache = true,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WebPageContent>();

        foreach (var url in urls)
        {
            try
            {
                var content = await FetchPageAsync(url, useCache, cancellationToken);
                results.Add(content);
            }
            catch
            {
                // Skip failed URLs
            }
        }

        return results;
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(_cachePath))
        {
            var files = Directory.GetFiles(_cachePath, "*.json");
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }

        await Task.CompletedTask;
    }

    public Task<long> GetCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_cachePath))
            return Task.FromResult(0L);

        var size = Directory.GetFiles(_cachePath, "*.json")
            .Sum(f => new FileInfo(f).Length);

        return Task.FromResult(size);
    }

    private string GetCacheKey(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).Substring(0, 16);
    }

    private async Task<T?> GetFromCacheAsync<T>(string key) where T : class
    {
        var path = Path.Combine(_cachePath, $"{key}.json");
        if (!File.Exists(path))
            return null;

        var fileInfo = new FileInfo(path);
        if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromHours(_cacheExpirationHours))
        {
            File.Delete(path);
            return null;
        }

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<T>(json);
    }

    private async Task SaveToCacheAsync<T>(string key, T data)
    {
        var path = Path.Combine(_cachePath, $"{key}.json");
        var json = JsonSerializer.Serialize(data);
        await File.WriteAllTextAsync(path, json);
    }
}
