namespace Nemesis.Shared.DTOs;

public class WebSearchResult
{
    public string Query { get; set; } = string.Empty;
    public List<SearchResultItem> Results { get; set; } = new();
    public DateTime SearchedAt { get; set; } = DateTime.UtcNow;
    public bool FromCache { get; set; }
}

public class SearchResultItem
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public string? CachedContent { get; set; }
    public DateTime? CachedAt { get; set; }
}

public class WebPageContent
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    public bool FromCache { get; set; }
    public List<string> CodeBlocks { get; set; } = new();
}
