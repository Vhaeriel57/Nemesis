using System.Text;
using Nemesis.CodeAnalysis.Models;

namespace Nemesis.AgentCore.RAG;

public class CodeChunker
{
    private readonly int _maxChunkSize;
    private readonly int _overlapSize;

    public CodeChunker(int maxChunkSize = 1500, int overlapSize = 200)
    {
        _maxChunkSize = maxChunkSize;
        _overlapSize = overlapSize;
    }

    public List<CodeChunk> ChunkFile(CodeFile file)
    {
        var chunks = new List<CodeChunk>();

        // Chunk by type/member for better semantic boundaries
        if (file.Types.Any())
        {
            foreach (var type in file.Types)
            {
                // Add type-level chunk
                chunks.Add(CreateTypeChunk(file, type));

                // Add member-level chunks for large types
                foreach (var member in type.Members)
                {
                    if (!string.IsNullOrEmpty(member.Body) && member.Body.Length > 100)
                    {
                        chunks.Add(CreateMemberChunk(file, type, member));
                    }
                }
            }
        }
        else
        {
            // Fallback to line-based chunking for files without clear type structure
            chunks.AddRange(ChunkByLines(file));
        }

        return chunks;
    }

    private CodeChunk CreateTypeChunk(CodeFile file, TypeInfo type)
    {
        var lines = file.Content.Split('\n');
        var startIndex = Math.Max(0, type.StartLine - 1);
        var endIndex = Math.Min(lines.Length, type.EndLine);
        var content = string.Join('\n', lines.Skip(startIndex).Take(endIndex - startIndex));

        // Truncate if too large
        if (content.Length > _maxChunkSize)
        {
            content = TruncateWithContext(content, _maxChunkSize);
        }

        return new CodeChunk
        {
            Id = $"{file.FilePath}::{type.FullName}",
            FilePath = file.FilePath,
            Content = content,
            StartLine = type.StartLine,
            EndLine = type.EndLine,
            ChunkType = ChunkType.Type,
            SymbolName = type.FullName,
            Metadata = new Dictionary<string, string>
            {
                ["kind"] = type.Kind,
                ["namespace"] = type.Namespace ?? "",
                ["file"] = file.RelativePath
            }
        };
    }

    private CodeChunk CreateMemberChunk(CodeFile file, TypeInfo type, MemberInfo member)
    {
        var lines = file.Content.Split('\n');
        var startIndex = Math.Max(0, member.StartLine - 1);
        var endIndex = Math.Min(lines.Length, member.EndLine);
        var content = string.Join('\n', lines.Skip(startIndex).Take(endIndex - startIndex));

        if (content.Length > _maxChunkSize)
        {
            content = TruncateWithContext(content, _maxChunkSize);
        }

        return new CodeChunk
        {
            Id = $"{file.FilePath}::{type.FullName}.{member.Name}",
            FilePath = file.FilePath,
            Content = content,
            StartLine = member.StartLine,
            EndLine = member.EndLine,
            ChunkType = ChunkType.Member,
            SymbolName = $"{type.FullName}.{member.Name}",
            Metadata = new Dictionary<string, string>
            {
                ["kind"] = member.Kind,
                ["returnType"] = member.ReturnType ?? "",
                ["parentType"] = type.FullName,
                ["file"] = file.RelativePath
            }
        };
    }

    private List<CodeChunk> ChunkByLines(CodeFile file)
    {
        var chunks = new List<CodeChunk>();
        var lines = file.Content.Split('\n');
        var currentChunk = new StringBuilder();
        var chunkStartLine = 1;
        var chunkIndex = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (currentChunk.Length + line.Length + 1 > _maxChunkSize && currentChunk.Length > 0)
            {
                chunks.Add(new CodeChunk
                {
                    Id = $"{file.FilePath}::chunk_{chunkIndex}",
                    FilePath = file.FilePath,
                    Content = currentChunk.ToString(),
                    StartLine = chunkStartLine,
                    EndLine = i,
                    ChunkType = ChunkType.Lines,
                    Metadata = new Dictionary<string, string>
                    {
                        ["file"] = file.RelativePath
                    }
                });

                // Start new chunk with overlap
                var overlapStart = Math.Max(0, i - _overlapSize / 50);
                currentChunk.Clear();
                for (int j = overlapStart; j < i; j++)
                {
                    currentChunk.AppendLine(lines[j]);
                }
                chunkStartLine = overlapStart + 1;
                chunkIndex++;
            }

            currentChunk.AppendLine(line);
        }

        // Add remaining content
        if (currentChunk.Length > 0)
        {
            chunks.Add(new CodeChunk
            {
                Id = $"{file.FilePath}::chunk_{chunkIndex}",
                FilePath = file.FilePath,
                Content = currentChunk.ToString(),
                StartLine = chunkStartLine,
                EndLine = lines.Length,
                ChunkType = ChunkType.Lines,
                Metadata = new Dictionary<string, string>
                {
                    ["file"] = file.RelativePath
                }
            });
        }

        return chunks;
    }

    private string TruncateWithContext(string content, int maxSize)
    {
        if (content.Length <= maxSize)
            return content;

        // Keep the first part (signature, class declaration) and add truncation notice
        var truncated = content.Substring(0, maxSize - 50);
        var lastNewline = truncated.LastIndexOf('\n');
        if (lastNewline > maxSize / 2)
        {
            truncated = truncated.Substring(0, lastNewline);
        }

        return truncated + "\n// ... [truncated]";
    }

    public string CreateSearchableText(CodeChunk chunk)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(chunk.SymbolName))
        {
            sb.AppendLine($"Symbol: {chunk.SymbolName}");
        }

        if (chunk.Metadata.TryGetValue("kind", out var kind))
        {
            sb.AppendLine($"Kind: {kind}");
        }

        if (chunk.Metadata.TryGetValue("file", out var file))
        {
            sb.AppendLine($"File: {file}");
        }

        sb.AppendLine();
        sb.AppendLine(chunk.Content);

        return sb.ToString();
    }
}

public class CodeChunk
{
    public string Id { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public ChunkType ChunkType { get; set; }
    public string? SymbolName { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public enum ChunkType
{
    Type,
    Member,
    Lines
}
