using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nemesis.CodeAnalysis.Models;
using Nemesis.Shared.DTOs;
using TypeInfo = Nemesis.CodeAnalysis.Models.TypeInfo;

namespace Nemesis.CodeAnalysis.Analysis;

public class RoslynAnalyzer
{
    private static readonly string[] UnityAssemblyPaths = new[]
    {
        "UnityEngine.dll",
        "UnityEngine.CoreModule.dll",
        "UnityEditor.dll"
    };

    public async Task<CodeFile> AnalyzeFileAsync(
        CodeFile file,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(file.Content))
            return file;

        try
        {
            var tree = CSharpSyntaxTree.ParseText(file.Content, cancellationToken: cancellationToken);
            var root = await tree.GetRootAsync(cancellationToken);
            var compilation = CreateCompilation(tree);
            var semanticModel = compilation.GetSemanticModel(tree);

            file.Usings = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Select(u => u.Name?.ToString() ?? string.Empty)
                .Where(u => !string.IsNullOrEmpty(u))
                .ToList();

            var namespaceDecl = root.DescendantNodes()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault();
            file.Namespace = namespaceDecl?.Name.ToString();

            file.Types = AnalyzeTypes(root, semanticModel, file.FilePath);

            var diagnostics = tree.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToList();
            file.Errors.AddRange(diagnostics);
        }
        catch (Exception ex)
        {
            file.Errors.Add($"Analysis error: {ex.Message}");
        }

        return file;
    }

    private List<TypeInfo> AnalyzeTypes(SyntaxNode root, SemanticModel semanticModel, string filePath)
    {
        var types = new List<TypeInfo>();

        var typeDeclarations = root.DescendantNodes()
            .Where(n => n is TypeDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax);

        foreach (var typeNode in typeDeclarations)
        {
            var typeInfo = typeNode switch
            {
                ClassDeclarationSyntax c => AnalyzeClassOrStruct(c, semanticModel, filePath, "class"),
                StructDeclarationSyntax s => AnalyzeClassOrStruct(s, semanticModel, filePath, "struct"),
                InterfaceDeclarationSyntax i => AnalyzeInterface(i, semanticModel, filePath),
                RecordDeclarationSyntax r => AnalyzeRecord(r, semanticModel, filePath),
                EnumDeclarationSyntax e => AnalyzeEnum(e, semanticModel, filePath),
                DelegateDeclarationSyntax d => AnalyzeDelegate(d, semanticModel, filePath),
                _ => null
            };

            if (typeInfo != null)
                types.Add(typeInfo);
        }

        return types;
    }

    private TypeInfo AnalyzeClassOrStruct(TypeDeclarationSyntax syntax, SemanticModel model, string filePath, string kind)
    {
        var symbol = model.GetDeclaredSymbol(syntax);
        var lineSpan = syntax.GetLocation().GetLineSpan();

        var typeInfo = new TypeInfo
        {
            Name = syntax.Identifier.Text,
            FullName = symbol?.ToDisplayString() ?? syntax.Identifier.Text,
            Kind = kind,
            Namespace = symbol?.ContainingNamespace?.ToDisplayString(),
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            BaseType = syntax.BaseList?.Types.FirstOrDefault()?.Type.ToString(),
            Interfaces = GetInterfaces(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists),
            Members = AnalyzeMembers(syntax, model)
        };

        return typeInfo;
    }

    private TypeInfo AnalyzeInterface(InterfaceDeclarationSyntax syntax, SemanticModel model, string filePath)
    {
        var symbol = model.GetDeclaredSymbol(syntax);
        var lineSpan = syntax.GetLocation().GetLineSpan();

        return new TypeInfo
        {
            Name = syntax.Identifier.Text,
            FullName = symbol?.ToDisplayString() ?? syntax.Identifier.Text,
            Kind = "interface",
            Namespace = symbol?.ContainingNamespace?.ToDisplayString(),
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            Interfaces = GetInterfaces(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists),
            Members = AnalyzeMembers(syntax, model)
        };
    }

    private TypeInfo AnalyzeRecord(RecordDeclarationSyntax syntax, SemanticModel model, string filePath)
    {
        var symbol = model.GetDeclaredSymbol(syntax);
        var lineSpan = syntax.GetLocation().GetLineSpan();

        return new TypeInfo
        {
            Name = syntax.Identifier.Text,
            FullName = symbol?.ToDisplayString() ?? syntax.Identifier.Text,
            Kind = "record",
            Namespace = symbol?.ContainingNamespace?.ToDisplayString(),
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            BaseType = syntax.BaseList?.Types.FirstOrDefault()?.Type.ToString(),
            Interfaces = GetInterfaces(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists),
            Members = AnalyzeMembers(syntax, model)
        };
    }

    private TypeInfo AnalyzeEnum(EnumDeclarationSyntax syntax, SemanticModel model, string filePath)
    {
        var symbol = model.GetDeclaredSymbol(syntax);
        var lineSpan = syntax.GetLocation().GetLineSpan();

        var members = syntax.Members.Select(m => new MemberInfo
        {
            Name = m.Identifier.Text,
            Kind = "EnumMember",
            StartLine = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            EndLine = m.GetLocation().GetLineSpan().EndLinePosition.Line + 1
        }).ToList();

        return new TypeInfo
        {
            Name = syntax.Identifier.Text,
            FullName = symbol?.ToDisplayString() ?? syntax.Identifier.Text,
            Kind = "enum",
            Namespace = symbol?.ContainingNamespace?.ToDisplayString(),
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists),
            Members = members
        };
    }

    private TypeInfo AnalyzeDelegate(DelegateDeclarationSyntax syntax, SemanticModel model, string filePath)
    {
        var symbol = model.GetDeclaredSymbol(syntax);
        var lineSpan = syntax.GetLocation().GetLineSpan();

        return new TypeInfo
        {
            Name = syntax.Identifier.Text,
            FullName = symbol?.ToDisplayString() ?? syntax.Identifier.Text,
            Kind = "delegate",
            Namespace = symbol?.ContainingNamespace?.ToDisplayString(),
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists)
        };
    }

    private List<MemberInfo> AnalyzeMembers(TypeDeclarationSyntax syntax, SemanticModel model)
    {
        var members = new List<MemberInfo>();

        foreach (var member in syntax.Members)
        {
            var memberInfo = member switch
            {
                MethodDeclarationSyntax m => AnalyzeMethod(m, model),
                PropertyDeclarationSyntax p => AnalyzeProperty(p, model),
                FieldDeclarationSyntax f => AnalyzeField(f, model),
                ConstructorDeclarationSyntax c => AnalyzeConstructor(c, model),
                EventDeclarationSyntax e => AnalyzeEvent(e, model),
                IndexerDeclarationSyntax i => AnalyzeIndexer(i, model),
                _ => null
            };

            if (memberInfo != null)
                members.Add(memberInfo);
        }

        return members;
    }

    private MemberInfo AnalyzeMethod(MethodDeclarationSyntax syntax, SemanticModel model)
    {
        var lineSpan = syntax.GetLocation().GetLineSpan();

        return new MemberInfo
        {
            Name = syntax.Identifier.Text,
            Kind = "Method",
            ReturnType = syntax.ReturnType.ToString(),
            Parameters = syntax.ParameterList.Parameters.Select(p => new ParameterInfo
            {
                Name = p.Identifier.Text,
                Type = p.Type?.ToString() ?? "object",
                DefaultValue = p.Default?.Value.ToString(),
                IsOptional = p.Default != null,
                IsParams = p.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)),
                IsRef = p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)),
                IsOut = p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword))
            }).ToList(),
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists),
            Body = syntax.Body?.ToString() ?? syntax.ExpressionBody?.ToString()
        };
    }

    private MemberInfo AnalyzeProperty(PropertyDeclarationSyntax syntax, SemanticModel model)
    {
        var lineSpan = syntax.GetLocation().GetLineSpan();

        return new MemberInfo
        {
            Name = syntax.Identifier.Text,
            Kind = "Property",
            ReturnType = syntax.Type.ToString(),
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists)
        };
    }

    private MemberInfo AnalyzeField(FieldDeclarationSyntax syntax, SemanticModel model)
    {
        var lineSpan = syntax.GetLocation().GetLineSpan();
        var variable = syntax.Declaration.Variables.FirstOrDefault();

        return new MemberInfo
        {
            Name = variable?.Identifier.Text ?? "unknown",
            Kind = "Field",
            ReturnType = syntax.Declaration.Type.ToString(),
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists)
        };
    }

    private MemberInfo AnalyzeConstructor(ConstructorDeclarationSyntax syntax, SemanticModel model)
    {
        var lineSpan = syntax.GetLocation().GetLineSpan();

        return new MemberInfo
        {
            Name = syntax.Identifier.Text,
            Kind = "Constructor",
            Parameters = syntax.ParameterList.Parameters.Select(p => new ParameterInfo
            {
                Name = p.Identifier.Text,
                Type = p.Type?.ToString() ?? "object",
                DefaultValue = p.Default?.Value.ToString(),
                IsOptional = p.Default != null
            }).ToList(),
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists),
            Body = syntax.Body?.ToString()
        };
    }

    private MemberInfo AnalyzeEvent(EventDeclarationSyntax syntax, SemanticModel model)
    {
        var lineSpan = syntax.GetLocation().GetLineSpan();

        return new MemberInfo
        {
            Name = syntax.Identifier.Text,
            Kind = "Event",
            ReturnType = syntax.Type.ToString(),
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists)
        };
    }

    private MemberInfo AnalyzeIndexer(IndexerDeclarationSyntax syntax, SemanticModel model)
    {
        var lineSpan = syntax.GetLocation().GetLineSpan();

        return new MemberInfo
        {
            Name = "this[]",
            Kind = "Indexer",
            ReturnType = syntax.Type.ToString(),
            Parameters = syntax.ParameterList.Parameters.Select(p => new ParameterInfo
            {
                Name = p.Identifier.Text,
                Type = p.Type?.ToString() ?? "object"
            }).ToList(),
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetDocumentation(syntax),
            Modifiers = syntax.Modifiers.Select(m => m.Text).ToList(),
            Attributes = GetAttributes(syntax.AttributeLists)
        };
    }

    private static CSharpCompilation CreateCompilation(SyntaxTree tree)
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location)
        };

        // Try to add runtime assemblies
        var runtimePath = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimePath != null)
        {
            var systemRuntime = Path.Combine(runtimePath, "System.Runtime.dll");
            if (File.Exists(systemRuntime))
                references.Add(MetadataReference.CreateFromFile(systemRuntime));
        }

        return CSharpCompilation.Create("Analysis", new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static string? GetDocumentation(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                       t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Select(t => t.ToString().Trim())
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(trivia) ? null : trivia;
    }

    private static List<string> GetInterfaces(TypeDeclarationSyntax syntax)
    {
        if (syntax.BaseList == null)
            return new List<string>();

        return syntax.BaseList.Types
            .Skip(1) // First is usually base class
            .Select(t => t.Type.ToString())
            .ToList();
    }

    private static List<string> GetAttributes(SyntaxList<AttributeListSyntax> attributeLists)
    {
        return attributeLists
            .SelectMany(al => al.Attributes)
            .Select(a => a.Name.ToString())
            .ToList();
    }
}
