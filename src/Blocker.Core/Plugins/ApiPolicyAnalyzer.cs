using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using Blocker.Sdk;
using Blocker.Sdk.Events;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Blocker.Core.Plugins;

/// <summary>A problem found in plugin source: a policy violation or a compiler error.</summary>
/// <param name="File">Path of the source file inside the package; empty when the problem has no location.</param>
/// <param name="Line">1-based line number; 0 when the problem has no location.</param>
/// <param name="RuleId">Policy rule id (<c>BLK###</c>) or compiler id (<c>CS####</c>).</param>
/// <param name="Message">User-facing explanation.</param>
public sealed record PluginDiagnostic(string File, int Line, string RuleId, string Message)
{
    /// <summary>Formats the diagnostic as <c>file:line: id: message</c>.</summary>
    /// <returns>The formatted text.</returns>
    public override string ToString() => File.Length == 0 ? $"{RuleId}: {Message}" : $"{File}:{Line}: {RuleId}: {Message}";
}

/// <summary>
/// Semantic API policy for plugin code. Every symbol a plugin references (names, object creations, attributes) is
/// matched against <see cref="ApiPolicyRules"/>; <c>typeof(...)</c> and <c>nameof(...)</c> are always fine because they
/// only name a type. Unsafe and native-interop syntax is rejected outright. Extension interfaces and capability-gated
/// SDK calls (BLK008) must have their capability in the manifest.
/// </summary>
public static class ApiPolicyAnalyzer
{
    private const int MaxDiagnostics = 200;

    private static readonly FrozenDictionary<SyntaxKind, SyntaxRule> SyntaxRulesByKind =
        ApiPolicyRules.Syntax.ToFrozenDictionary(rule => rule.Kind);

    /// <summary>Checks every syntax tree of <paramref name="compilation"/> against the API policy.</summary>
    /// <param name="compilation">The plugin compilation. It does not need to be error-free.</param>
    /// <param name="capabilities">Capabilities declared in the manifest; they lift the rules they unlock.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <returns>Violations ordered by file and line; empty when the code complies.</returns>
    public static IReadOnlyList<PluginDiagnostic> Analyze(
        CSharpCompilation compilation,
        IReadOnlySet<PluginCapability> capabilities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(capabilities);
        var run = new Run(capabilities);
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SyntaxNode root = tree.GetRoot(cancellationToken);
            run.CheckSyntax(tree, root, cancellationToken);
            run.CheckSymbols(tree, root, compilation.GetSemanticModel(tree), cancellationToken);
        }

        return [.. run.Diagnostics.OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.RuleId, StringComparer.Ordinal)];
    }

    private sealed class Run(IReadOnlySet<PluginCapability> capabilities)
    {
        private readonly HashSet<(string File, int Line, string Rule)> _seen = [];

        public List<PluginDiagnostic> Diagnostics { get; } = [];

        public void CheckSyntax(SyntaxTree tree, SyntaxNode root, CancellationToken cancellationToken)
        {
            foreach (SyntaxNodeOrToken item in root.DescendantNodesAndTokens())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SyntaxRulesByKind.TryGetValue(item.Kind(), out SyntaxRule? rule))
                {
                    Add(tree, tree.GetLocation(item.Span), rule.Id, rule.Reason);
                }
            }
        }

        public void CheckSymbols(SyntaxTree tree, SyntaxNode root, SemanticModel model, CancellationToken cancellationToken)
        {
            // typeof(X) and nameof(X) only name a type, so their operands are not references to it.
            bool Descend(SyntaxNode node) =>
                node is not TypeOfExpressionSyntax
                && !(node is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } call
                    && model.GetSymbolInfo(call, cancellationToken).Symbol is null);

            foreach (SyntaxNode node in root.DescendantNodes(Descend))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckCapabilities(tree, node, model, cancellationToken);
                if (node is SimpleNameSyntax or BaseObjectCreationExpressionSyntax or AttributeSyntax)
                {
                    CheckReference(tree, node, model.GetSymbolInfo(node, cancellationToken));
                }
                else if (IsAsync(node) && IsVoidMethod(node, model, cancellationToken))
                {
                    Add(tree, node.GetLocation(), ApiPolicyRules.AsyncVoidId, ApiPolicyRules.AsyncVoidReason);
                }
            }
        }

        private void CheckCapabilities(SyntaxTree tree, SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            if (node is TypeDeclarationSyntax declaration && model.GetDeclaredSymbol(declaration, cancellationToken) is { } type)
            {
                List<string> missing = [];
                foreach (INamedTypeSymbol contract in type.AllInterfaces)
                {
                    if (ApiPolicyRules.ExtensionInterfaces.TryGetValue(FullName(contract.OriginalDefinition), out PluginCapability needed)
                        && !capabilities.Contains(needed))
                    {
                        missing.Add($"'{PluginCapabilities.ToManifestName(needed)}' (for {contract.Name})");
                    }
                }

                if (missing.Count > 0)
                {
                    missing.Sort(StringComparer.Ordinal);
                    Add(tree, declaration.Identifier.GetLocation(), ApiPolicyRules.MissingCapabilityId,
                        $"Class '{type.Name}' needs {string.Join(", ", missing)}; declare it in plugin.json or remove the interface.");
                }
            }
            else if (node is InvocationExpressionSyntax invocation
                && model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol { ContainingType: { } owner } method)
            {
                string ownerName = FullName(owner.OriginalDefinition);
                CapabilityCall? call = ApiPolicyRules.CapabilityCalls.FirstOrDefault(c => c.Type == ownerName && c.Method == method.Name);
                if (call is null)
                {
                    return;
                }

                PluginCapability? needed = call.Capability
                    ?? (method.TypeArguments is [INamedTypeSymbol eventType] ? CoreEventCapability(eventType) : null);
                if (needed is { } capability && !capabilities.Contains(capability))
                {
                    Location location = invocation.Expression is MemberAccessExpressionSyntax access ? access.Name.GetLocation() : invocation.GetLocation();
                    Add(tree, location, ApiPolicyRules.MissingCapabilityId,
                        $"'{owner.Name}.{method.Name}' needs the '{PluginCapabilities.ToManifestName(capability)}' capability; declare it in plugin.json.");
                }
            }
        }

        /// <summary>The capability needed for a core SDK event type; <see langword="null"/> for any other type.</summary>
        private static PluginCapability? CoreEventCapability(INamedTypeSymbol eventType)
        {
            Assembly sdk = typeof(IBlockerEvent).Assembly;
            if (eventType.ContainingAssembly?.Name != sdk.GetName().Name || sdk.GetType(eventType.ToDisplayString(), throwOnError: false) is not { } type)
            {
                return null;
            }

            try
            {
                return PluginCapabilities.RequiredForEvent(type);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool IsAsync(SyntaxNode node) => node switch
        {
            MethodDeclarationSyntax method => method.Modifiers.Any(SyntaxKind.AsyncKeyword),
            LocalFunctionStatementSyntax local => local.Modifiers.Any(SyntaxKind.AsyncKeyword),
            AnonymousFunctionExpressionSyntax lambda => lambda.Modifiers.Any(SyntaxKind.AsyncKeyword),
            _ => false,
        };

        private static bool IsVoidMethod(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            ISymbol? symbol = node is AnonymousFunctionExpressionSyntax
                ? model.GetSymbolInfo(node, cancellationToken).Symbol
                : model.GetDeclaredSymbol(node, cancellationToken);
            return symbol is IMethodSymbol { ReturnsVoid: true };
        }

        private void CheckReference(SyntaxTree tree, SyntaxNode node, SymbolInfo info)
        {
            ImmutableArray<ISymbol> symbols = info.Symbol is { } single ? [single] : info.CandidateSymbols;
            foreach (ISymbol symbol in symbols)
            {
                if (symbol is IDynamicTypeSymbol)
                {
                    Add(tree, node.GetLocation(), ApiPolicyRules.DynamicId, ApiPolicyRules.DynamicReason);
                    return;
                }

                ISymbol target = Normalize(symbol);
                foreach (ApiRule rule in ApiPolicyRules.All)
                {
                    if (Matches(rule, target))
                    {
                        Add(tree, node.GetLocation(), rule.Id, $"'{Describe(target)}' is not allowed: {rule.Reason}");
                        return;
                    }
                }
            }
        }

        private bool Matches(ApiRule rule, ISymbol symbol)
        {
            if (rule.UnlockedBy is { } unlock && capabilities.Contains(unlock))
            {
                return false;
            }

            INamedTypeSymbol? type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            if (type is null || symbol.Kind is SymbolKind.TypeParameter or SymbolKind.Namespace)
            {
                return false;
            }

            string typeName = FullName(type);
            bool selected = rule.Scope switch
            {
                ApiRuleScope.Namespace => InNamespace(type, rule.Target) && rule.Except?.Contains(typeName) != true,
                ApiRuleScope.Type => typeName == rule.Target,
                ApiRuleScope.TypePrefix => typeName.StartsWith(rule.Target, StringComparison.Ordinal),
                _ => typeName == rule.Target && symbol is not INamedTypeSymbol && IsDeniedMember(rule, MemberName(symbol)),
            };
            return selected && (rule.Where?.Invoke(symbol) ?? true);
        }

        private static bool IsDeniedMember(ApiRule rule, string member) =>
            rule.Members is { } members ? members.Contains(member) : rule.Except?.Contains(member) != true;

        private static bool InNamespace(INamedTypeSymbol type, string target)
        {
            string ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return ns == target || ns.StartsWith(target + ".", StringComparison.Ordinal);
        }

        private static ISymbol Normalize(ISymbol symbol) => symbol switch
        {
            IAliasSymbol alias => Normalize(alias.Target),
            IMethodSymbol { ReducedFrom: { } reduced } => reduced.OriginalDefinition,
            _ => symbol.OriginalDefinition,
        };

        private static string MemberName(ISymbol symbol) => symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ? ".ctor" : symbol.Name;

        private static string FullName(INamedTypeSymbol type)
        {
            string name = type.ContainingType is { } outer ? $"{FullName(outer)}.{type.Name}" : type.Name;
            return type.ContainingType is null && type.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? $"{ns.ToDisplayString()}.{name}"
                : name;
        }

        private static string Describe(ISymbol symbol) => symbol switch
        {
            INamedTypeSymbol type => FullName(type),
            { ContainingType: { } owner } when MemberName(symbol) == ".ctor" => FullName(owner),
            { ContainingType: { } owner } => $"{FullName(owner)}.{symbol.Name}",
            _ => symbol.Name,
        };

        private void Add(SyntaxTree tree, Location location, string ruleId, string message)
        {
            int line = location.GetLineSpan().StartLinePosition.Line + 1;
            if (Diagnostics.Count < MaxDiagnostics && _seen.Add((tree.FilePath, line, ruleId)))
            {
                Diagnostics.Add(new PluginDiagnostic(tree.FilePath, line, ruleId, message));
            }
        }
    }
}
