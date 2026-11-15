#:package Microsoft.CodeAnalysis.CSharp@5.0.0
// Removes game keys and decryption constants from a CUE4Parse checkout, by path and symbol, never by value.
//   dotnet run strip.cs -- <CUE4Parse project folder> <spec file>...
// Spec lines (paths relative to the project folder, * and ** globs):
//   gut <glob>                       every body and initializer in those files: methods throw, fields default
//   gut-members <file> <name>...     only those members
//   gut-if <file> <GAME_X>...        if statements whose condition names one of those games: the body throws
//   empty <file>                     the file is truncated to zero bytes (embedded resources)
// Every file touched loses its comments. A spec line that matches nothing fails the run.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

if (args.Length < 2) { Console.Error.WriteLine("usage: strip.cs <project folder> <spec>..."); return 2; }
var root = Path.GetFullPath(args[0]);
var failed = false;
foreach (var spec in args[1..])
foreach (var raw in File.ReadLines(spec))
{
    var line = raw.Split('#')[0].Trim();
    if (line.Length == 0) continue;
    var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    var files = Glob(root, f[1]);
    if (files.Count == 0) { Console.Error.WriteLine($"no file: {line}"); failed = true; continue; }
    foreach (var path in files)
    {
        if (f[0] == "empty") { File.WriteAllBytes(path, []); Console.WriteLine($"emptied {Rel(path)}"); continue; }
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
        var rw = new Gut(f[0], f[2..].ToHashSet());
        if (f[0] is not ("gut" or "gut-members" or "gut-if")) { Console.Error.WriteLine($"unknown op: {line}"); return 2; }
        var node = rw.Visit(tree.GetRoot());
        var missing = f[0] == "gut" ? [] : f[2..].Except(rw.Hit).ToList();
        if (rw.Count == 0 || missing.Count > 0) { Console.Error.WriteLine($"nothing for {string.Join(' ', missing)} in {Rel(path)}: {line}"); failed = true; }
        node = new NoComments().Visit(node).NormalizeWhitespace(eol: "\n");
        File.WriteAllText(path, node.ToFullString().ReplaceLineEndings("\n"));
        Console.WriteLine($"{f[0]} {Rel(path)}: {rw.Count} members");
    }
}
return failed ? 1 : 0;

string Rel(string p) => Path.GetRelativePath(root, p).Replace('\\', '/');

static List<string> Glob(string root, string pattern)
{
    var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern)
        .Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$");
    return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => rx.IsMatch(Path.GetRelativePath(root, p).Replace('\\', '/'))).Order(StringComparer.Ordinal).ToList();
}

sealed class Gut(string op, HashSet<string> names) : CSharpSyntaxRewriter
{
    public readonly HashSet<string> Hit = [];
    public int Count;

    static readonly ThrowStatementSyntax Throw = ThrowStatement(Thrown()).NormalizeWhitespace();
    static ExpressionSyntax Thrown() => ParseExpression("new System.NotSupportedException(\"needs a key SCSKiller doesn't ship\")");

    bool Take(string name)
    {
        if (op == "gut") { Count++; return true; }
        if (op != "gut-members" || !names.Contains(name)) return false;
        Hit.Add(name);
        Count++;
        return true;
    }

    T Body<T>(T node, BlockSyntax? body, ArrowExpressionClauseSyntax? arrow, Func<T, BlockSyntax, T> withBody, Func<T, ArrowExpressionClauseSyntax, T> withArrow, bool empty = false)
    {
        if (body != null) return withBody(node, empty ? Block() : Block(Throw));
        if (arrow != null) return withArrow(node, ArrowExpressionClause(ThrowExpression(Thrown())));
        return node;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax n) =>
        Take(n.Identifier.Text) ? Body(n, n.Body, n.ExpressionBody, (x, b) => x.WithBody(b), (x, a) => x.WithExpressionBody(a)) : base.VisitMethodDeclaration(n);

    // a static constructor that throws would break every game that touches the type: emptied instead
    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax n) =>
        Take(n.Identifier.Text) ? Body(n, n.Body, n.ExpressionBody, (x, b) => x.WithBody(b), (x, a) => x.WithExpressionBody(a), n.Modifiers.Any(SyntaxKind.StaticKeyword)) : base.VisitConstructorDeclaration(n);

    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax n) =>
        Take(n.OperatorToken.Text) ? Body(n, n.Body, n.ExpressionBody, (x, b) => x.WithBody(b), (x, a) => x.WithExpressionBody(a)) : base.VisitOperatorDeclaration(n);

    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax n) =>
        Take(n.Type.ToString()) ? Body(n, n.Body, n.ExpressionBody, (x, b) => x.WithBody(b), (x, a) => x.WithExpressionBody(a)) : base.VisitConversionOperatorDeclaration(n);

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax n)
    {
        if (!Take(n.Identifier.Text)) return base.VisitPropertyDeclaration(n);
        if (n.ExpressionBody != null) return n.WithExpressionBody(ArrowExpressionClause(ThrowExpression(Thrown())));
        if (n.AccessorList != null)
            n = n.WithAccessorList(n.AccessorList.WithAccessors(List(n.AccessorList.Accessors.Select(a =>
                a.Body != null ? a.WithBody(Block(Throw)) : a.ExpressionBody != null ? a.WithExpressionBody(ArrowExpressionClause(ThrowExpression(Thrown()))) : a))));
        return n.Initializer != null ? n.WithInitializer(EqualsValueClause(Default())) : n;
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax n)
    {
        if (op != "gut") return base.VisitIndexerDeclaration(n);
        Count++;
        if (n.ExpressionBody != null) return n.WithExpressionBody(ArrowExpressionClause(ThrowExpression(Thrown())));
        return n.WithAccessorList(n.AccessorList!.WithAccessors(List(n.AccessorList.Accessors.Select(a => a.Body != null ? a.WithBody(Block(Throw)) : a))));
    }

    // const numbers stay (array sizes, InlineArray lengths): those named for a secret don't
    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax n)
    {
        var isConst = n.Modifiers.Any(SyntaxKind.ConstKeyword);
        var vars = n.Declaration.Variables.Select(v =>
        {
            if (v.Initializer == null || !Take(v.Identifier.Text)) return v;
            if (isConst && op == "gut" && n.Declaration.Type.ToString() != "string" && !Secret(v.Identifier.Text)) return v;
            return v.WithInitializer(EqualsValueClause(isConst ? LiteralExpression(SyntaxKind.DefaultLiteralExpression, Token(SyntaxKind.DefaultKeyword)) : Default()));
        });
        return n.WithDeclaration(n.Declaration.WithVariables(SeparatedList(vars)));
    }

    static bool Secret(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "key|xor|seed|secret|salt|iv$|^iv", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static ExpressionSyntax Default() => PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, LiteralExpression(SyntaxKind.DefaultLiteralExpression, Token(SyntaxKind.DefaultKeyword)));

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax n)
    {
        if (op == "gut-if")
        {
            var games = n.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text).Where(names.Contains).ToList();
            if (games.Count > 0)
            {
                Hit.UnionWith(games);
                Count++;
                return n.WithStatement(Block(Throw)).WithElse(n.Else is { } e ? (ElseClauseSyntax?)VisitElseClause(e) : null);
            }
        }
        return base.VisitIfStatement(n);
    }
}

sealed class NoComments() : CSharpSyntaxRewriter(visitIntoStructuredTrivia: false)
{
    public override SyntaxTrivia VisitTrivia(SyntaxTrivia t) => t.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
        or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia ? default : t;
}
