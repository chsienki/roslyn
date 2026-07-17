// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Razor.Language.Extensions;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Language;

public class MarkupSplitterTest
{
    private static MethodDeclarationIntermediateNode CreateRenderMethod()
        => new() { IsPrimaryMethod = true, Name = "BuildRenderTree" };

    private static ClassDeclarationIntermediateNode CreatePrimaryClass(params IntermediateNode[] children)
    {
        var @class = new ClassDeclarationIntermediateNode { IsPrimaryClass = true, Name = "TestComponent" };

        foreach (var child in children)
        {
            @class.Children.Add(child);
        }

        return @class;
    }

    private static CSharpCodeIntermediateNode CreateCSharpCode(string content)
    {
        var node = new CSharpCodeIntermediateNode();
        node.Children.Add(new CSharpIntermediateToken(content, source: null));
        return node;
    }

    [Fact]
    public void HasClassBodyMarkup_PureCSharp_ReturnsFalse()
    {
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(
            CreateCSharpCode("[Parameter] public int Count { get; set; }"),
            CreateCSharpCode("private void Increment() => Count++;"),
            renderMethod);

        Assert.False(MarkupSplitter.HasClassBodyMarkup(primaryClass, renderMethod));
    }

    [Fact]
    public void HasClassBodyMarkup_ClassBodyMarkupNode_ReturnsTrue()
    {
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(
            CreateCSharpCode("public RenderFragment Header => "),
            new MarkupElementIntermediateNode { TagName = "div" },
            CreateCSharpCode(";"),
            renderMethod);

        Assert.True(MarkupSplitter.HasClassBodyMarkup(primaryClass, renderMethod));
    }

    [Fact]
    public void HasClassBodyMarkup_SkipsRenderMethodAndSynthesizedHelpers()
    {
        var renderMethod = CreateRenderMethod();

        // The render method carries the component's top-level markup as its own children; that markup
        // is nested, not a class-body child, so it must not trip the gate. A synthesized helper that
        // happens to be markup-shaped must also be skipped.
        renderMethod.Children.Add(new MarkupElementIntermediateNode { TagName = "p" });

        var primaryClass = CreatePrimaryClass(
            CreateCSharpCode("private int _count;"),
            new MarkupElementIntermediateNode { TagName = "span", IsSynthesizedHelper = true },
            renderMethod);

        Assert.False(MarkupSplitter.HasClassBodyMarkup(primaryClass, renderMethod));
    }

    [Fact]
    public void IsClassBodyMarkup_CSharpAndStructuredDeclarations_AreNotMarkup()
    {
        Assert.False(MarkupSplitter.IsClassBodyMarkup(new CSharpCodeIntermediateNode()));
        Assert.False(MarkupSplitter.IsClassBodyMarkup(new FieldDeclarationIntermediateNode { Name = "_f", Type = "int" }));
        Assert.False(MarkupSplitter.IsClassBodyMarkup(
            new PropertyDeclarationIntermediateNode
            {
                Name = "P",
                Type = new CSharpIntermediateToken("int", source: null),
                ExpressionBody = "0",
            }));
        Assert.False(MarkupSplitter.IsClassBodyMarkup(new MethodDeclarationIntermediateNode()));
    }

    [Fact]
    public void IsClassBodyMarkup_MarkupNodes_AreMarkup()
    {
        Assert.True(MarkupSplitter.IsClassBodyMarkup(new MarkupElementIntermediateNode()));
        Assert.True(MarkupSplitter.IsClassBodyMarkup(new MarkupBlockIntermediateNode()));
        Assert.True(MarkupSplitter.IsClassBodyMarkup(new HtmlContentIntermediateNode()));
    }

    [Fact]
    public void Split_NoMarkup_ReturnsNoSplit()
    {
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(CreateCSharpCode("private int _count;"), renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, RazorParserOptions.Default);

        Assert.Same(SplitDecision.NoSplit, decision);
        Assert.False(decision.RequiresSplit);
        Assert.False(decision.IsFallback);
    }

    [Fact]
    public void Fallback_IsFallbackAndDoesNotRequireSplit()
    {
        var decision = SplitDecision.Fallback(FallbackReason.MarkupProperty);

        Assert.True(decision.IsFallback);
        Assert.False(decision.RequiresSplit);
        Assert.Equal(FallbackReason.MarkupProperty, decision.Reason);
    }

    [Fact]
    public void GetOrCreateDecision_MemoizesPerPrimaryClass()
    {
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(CreateCSharpCode("private int _count;"), renderMethod);

        var first = MarkupSplitter.GetOrCreateDecision(primaryClass, renderMethod, RazorParserOptions.Default);
        var second = MarkupSplitter.GetOrCreateDecision(primaryClass, renderMethod, RazorParserOptions.Default);

        Assert.Same(first, second);
    }

    [Fact]
    public void CollectClassBodyChildren_PreservesOrderAndExcludesRenderAndSynthesized()
    {
        var renderMethod = CreateRenderMethod();
        var first = CreateCSharpCode("private int _a;");
        var markup = new MarkupElementIntermediateNode { TagName = "div" };
        var last = CreateCSharpCode("private int _b;");
        var synthesized = new CSharpCodeIntermediateNode { IsSynthesizedHelper = true };

        var primaryClass = CreatePrimaryClass(first, synthesized, renderMethod, markup, last);

        var collected = MarkupSplitter.CollectClassBodyChildren(primaryClass, renderMethod);

        Assert.Equal(new IntermediateNode[] { first, markup, last }, collected);
    }


    [Fact]
    public void BuildAnalysisDocument_PureCSharp_EmitsTextWithNoMarkers()
    {
        var chunk = CreateCSharpCode("[Parameter] public int Count { get; set; }");
        var analysis = MarkupSplitter.BuildAnalysisDocument([chunk]);

        Assert.Contains("[Parameter] public int Count { get; set; }", analysis.Text);
        Assert.DoesNotContain(MarkupSplitter.MarkerMethodName, analysis.Text);
        var segment = Assert.Single(analysis.Segments);
        Assert.Equal(SegmentKind.CSharp, segment.Kind);
        Assert.Same(chunk, segment.Node);
    }

    [Fact]
    public void BuildAnalysisDocument_StatementMarkup_EmitsStatementMarker()
    {
        var before = CreateCSharpCode("void M(RenderTreeBuilder __builder) { ");
        var markup = new MarkupElementIntermediateNode { TagName = "ul" };
        var after = CreateCSharpCode(" }");

        var analysis = MarkupSplitter.BuildAnalysisDocument([before, markup, after]);

        Assert.Contains(MarkupSplitter.MarkerMethodName + "();", analysis.Text);
        Assert.Equal(3, analysis.Segments.Length);

        var markupSegment = analysis.Segments[1];
        Assert.Equal(SegmentKind.Markup, markupSegment.Kind);
        Assert.Same(markup, markupSegment.Node);

        // Segment offsets must index the emitted marker text exactly.
        var sliced = analysis.Text.Substring(markupSegment.Start, markupSegment.Length);
        Assert.Equal(MarkupSplitter.MarkerMethodName + "();", sliced);
    }

    [Fact]
    public void BuildAnalysisDocument_ExpressionTemplate_EmitsExpressionMarker()
    {
        var before = CreateCSharpCode("public RenderFragment Header => ");
        var template = new TemplateIntermediateNode();
        var after = CreateCSharpCode(";");

        var analysis = MarkupSplitter.BuildAnalysisDocument([before, template, after]);

        var markupSegment = analysis.Segments[1];
        Assert.Equal(SegmentKind.Markup, markupSegment.Kind);

        var sliced = analysis.Text.Substring(markupSegment.Start, markupSegment.Length);
        Assert.Equal(MarkupSplitter.MarkerMethodName + "()", sliced);

        // The whole document parses as valid C# (a get-only expression-bodied property).
        var tree = CSharpSyntaxTree.ParseText(analysis.Text);
        Assert.Empty(tree.GetDiagnostics());
    }

    [Fact]
    public void IsExpressionPositionMarkup_OnlyTemplateIsExpression()
    {
        Assert.True(MarkupSplitter.IsExpressionPositionMarkup(new TemplateIntermediateNode()));
        Assert.False(MarkupSplitter.IsExpressionPositionMarkup(new MarkupElementIntermediateNode()));
        Assert.False(MarkupSplitter.IsExpressionPositionMarkup(new MarkupBlockIntermediateNode()));
        Assert.False(MarkupSplitter.IsExpressionPositionMarkup(new HtmlContentIntermediateNode()));
    }

    private static System.Collections.Immutable.ImmutableArray<ClassifiedMember> ClassifyFromChildren(
        params IntermediateNode[] children)
    {
        var analysis = MarkupSplitter.BuildAnalysisDocument([.. children]);
        var classified = MarkupSplitter.ClassifyMembers(analysis, CSharpParseOptions.Default);
        Assert.NotNull(classified);
        return classified.Value;
    }

    [Fact]
    public void ClassifyMembers_PureCSharpField_IsNoMarkup()
    {
        var members = ClassifyFromChildren(CreateCSharpCode("private int _n;"));

        var member = Assert.Single(members);
        Assert.False(member.HasMarkup);
        Assert.Equal(MemberSplitKind.NoMarkup, member.Kind);
    }

    [Fact]
    public void ClassifyMembers_MarkupHelperMethod_IsMarkupMethod()
    {
        var members = ClassifyFromChildren(
            CreateCSharpCode("void M(RenderTreeBuilder __builder) { "),
            new MarkupElementIntermediateNode { TagName = "ul" },
            CreateCSharpCode(" }"));

        var member = Assert.Single(members);
        Assert.True(member.HasMarkup);
        Assert.Equal(MemberSplitKind.MarkupMethod, member.Kind);
    }

    [Fact]
    public void ClassifyMembers_MarkupExpressionProperty_IsMarkupProperty()
    {
        var members = ClassifyFromChildren(
            CreateCSharpCode("public RenderFragment Header => "),
            new TemplateIntermediateNode(),
            CreateCSharpCode(";"));

        var member = Assert.Single(members);
        Assert.True(member.HasMarkup);
        Assert.Equal(MemberSplitKind.MarkupProperty, member.Kind);
    }

    [Fact]
    public void ClassifyMembers_ExplicitInterfaceMarkupProperty_IsMarkupProperty()
    {
        var members = ClassifyFromChildren(
            CreateCSharpCode("global::Microsoft.AspNetCore.Components.RenderFragment IFoo.Bar => "),
            new TemplateIntermediateNode(),
            CreateCSharpCode(";"));

        // Any property/indexer with markup is classified as a markup property, which takes the whole file
        // to fallback -- explicit-interface properties included.
        var member = Assert.Single(members);
        Assert.Equal(MemberSplitKind.MarkupProperty, member.Kind);
    }

    [Fact]
    public void ClassifyMembers_MarkupField_IsMarkupUnsupported()
    {
        // A field's initializer runs in declaration order; lifting it across partials could perturb that,
        // so a markup field can't be lifted -- it takes the whole file to fallback.
        var members = ClassifyFromChildren(
            CreateCSharpCode("private RenderFragment _frag = "),
            new TemplateIntermediateNode(),
            CreateCSharpCode(";"));

        var member = Assert.Single(members);
        Assert.Equal(MemberSplitKind.MarkupUnsupported, member.Kind);
    }

    [Fact]
    public void ClassifyMembers_NestedTypeWithMarkup_IsMarkupUnsupported()
    {
        // A nested type carrying markup can't be lifted as if it were a method (it may be referenced from
        // decl, and its own markup members aren't handled), so it forces fallback.
        var members = ClassifyFromChildren(
            CreateCSharpCode("public class Nested { public RenderFragment View => "),
            new TemplateIntermediateNode(),
            CreateCSharpCode("; }"));

        var member = Assert.Single(members);
        Assert.Equal(MemberSplitKind.MarkupUnsupported, member.Kind);
    }

    [Fact]
    public void Split_MarkupField_FallsBack()
    {
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(
            CreateCSharpCode("private RenderFragment _frag = "),
            new TemplateIntermediateNode(),
            CreateCSharpCode(";"),
            renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, ParserOptions(LanguageVersion.CSharp13));

        var fallback = Assert.IsType<SplitDecision.SplitFallback>(decision);
        Assert.Equal(FallbackReason.UnsupportedMarkupMember, fallback.Reason);
    }

    [Fact]
    public void ClassifyMembers_MixedMembers_ClassifiesEachInOrder()
    {
        var members = ClassifyFromChildren(
            CreateCSharpCode("[Parameter] public int Count { get; set; } private void Helper(RenderTreeBuilder __builder) { "),
            new MarkupElementIntermediateNode { TagName = "div" },
            CreateCSharpCode(" } public RenderFragment Foo => "),
            new TemplateIntermediateNode(),
            CreateCSharpCode(";"));

        Assert.Equal(3, members.Length);
        Assert.Equal(MemberSplitKind.NoMarkup, members[0].Kind);     // int Count
        Assert.Equal(MemberSplitKind.MarkupMethod, members[1].Kind); // Helper
        Assert.Equal(MemberSplitKind.MarkupProperty, members[2].Kind); // Foo
    }

    [Fact]
    public void ClassifyMembers_UnrecoverableBraces_ReturnsNull()
    {
        // An unmatched open brace before markup: the class-body close brace goes missing, so the marker
        // ends up outside any recoverable member.
        var analysis = MarkupSplitter.BuildAnalysisDocument([
            CreateCSharpCode("void M(RenderTreeBuilder __builder) { "),
            new MarkupElementIntermediateNode { TagName = "div" }]);
        var classified = MarkupSplitter.ClassifyMembers(analysis, CSharpParseOptions.Default);

        Assert.Null(classified);
    }

    [Fact]
    public void SplitCSharpNode_NoCuts_ReturnsSameNode()
    {
        var node = CreateCSharpCode("private int _a;");
        var pieces = MarkupSplitter.SplitCSharpNode(node, System.Collections.Immutable.ImmutableArray<int>.Empty);
        Assert.Same(node, Assert.Single(pieces));
    }

    [Fact]
    public void SplitCSharpNode_SingleCut_SplitsContent()
    {
        var node = CreateCSharpCode("private int _a; void M() {");
        var pieces = MarkupSplitter.SplitCSharpNode(node, [15]);

        Assert.Equal(2, pieces.Length);
        Assert.Equal("private int _a;", TokenText(pieces[0]));
        Assert.Equal(" void M() {", TokenText(pieces[1]));
    }

    [Fact]
    public void SliceToken_RecomputesLineAndCharacterAcrossNewline()
    {
        // A token spanning two source lines, starting at line 3, char 4.
        var source = new SourceSpan(filePath: "C.razor", absoluteIndex: 100, lineIndex: 3, characterIndex: 4, length: 12);
        var token = new CSharpIntermediateToken("ab\ncdefghij", source);

        // Slice starting after the newline ("cdef...") must land on line 4, char 0.
        var sliced = MarkupSplitter.SliceToken(token, localStart: 3, localLength: 4);

        Assert.Equal("cdef", sliced.Content);
        var s = sliced.Source!.Value;
        Assert.Equal(103, s.AbsoluteIndex);   // 100 + 3
        Assert.Equal(4, s.LineIndex);         // advanced past one newline
        Assert.Equal(0, s.CharacterIndex);    // reset after the newline
        Assert.Equal(4, s.Length);
        Assert.Equal(0, s.LineCount);         // "cdef" is single-line: zero line breaks
        Assert.Equal(4, s.EndCharacterIndex);
    }

    [Fact]
    public void SliceToken_SpanningNewline_ReportsOneLineBreak()
    {
        // A slice that itself crosses a newline reports LineCount 1 (one line break), and its end
        // character resets on the new line.
        var source = new SourceSpan(filePath: "C.razor", absoluteIndex: 100, lineIndex: 3, characterIndex: 4, length: 12);
        var token = new CSharpIntermediateToken("ab\ncdefghij", source);

        var sliced = MarkupSplitter.SliceToken(token, localStart: 0, localLength: 5); // "ab\ncd"
        var s = sliced.Source!.Value;

        Assert.Equal("ab\ncd", sliced.Content);
        Assert.Equal(3, s.LineIndex);
        Assert.Equal(1, s.LineCount);         // one newline crossed
        Assert.Equal(2, s.EndCharacterIndex); // "cd" -> char 2 on the new line
    }

    [Fact]
    public void SliceToken_BeforeNewline_KeepsStartLineAndCharacter()
    {
        var source = new SourceSpan(filePath: "C.razor", absoluteIndex: 100, lineIndex: 3, characterIndex: 4, length: 12);
        var token = new CSharpIntermediateToken("ab\ncdefghij", source);

        var sliced = MarkupSplitter.SliceToken(token, localStart: 0, localLength: 2);

        Assert.Equal("ab", sliced.Content);
        var s = sliced.Source!.Value;
        Assert.Equal(100, s.AbsoluteIndex);
        Assert.Equal(3, s.LineIndex);
        Assert.Equal(4, s.CharacterIndex);
    }

    [Fact]
    public void SliceToken_NullSource_ProducesNullSource()
    {
        var token = new CSharpIntermediateToken("abcdef", source: null);
        var sliced = MarkupSplitter.SliceToken(token, 2, 3);
        Assert.Equal("cde", sliced.Content);
        Assert.Null(sliced.Source);
    }

    [Theory]
    [InlineData("abc", 0, 3, 3, 0, 3)]        // no newline: char advances by 3
    [InlineData("a\nb", 0, 3, 3, 1, 1)]       // one \n: line +1, char 1
    [InlineData("a\r\nb", 0, 4, 4, 1, 1)]     // \r\n counts once: line +1, char 1
    [InlineData("a\rb", 0, 3, 3, 1, 1)]       // lone \r counts as a break
    public void AdvanceLocation_CountsLineBreaks(string text, int start, int count, int expectedAbs, int expectedLine, int expectedChar)
    {
        var (abs, line, ch) = MarkupSplitter.AdvanceLocation(absolute: 0, line: 0, character: 0, text, start, count);
        Assert.Equal(expectedAbs, abs);
        Assert.Equal(expectedLine, line);
        Assert.Equal(expectedChar, ch);
    }

    [Fact]
    public void IsMarkupNode_RecognizesMarkupKinds()
    {
        Assert.True(MarkupSplitter.IsMarkupNode(new TemplateIntermediateNode()));
        Assert.True(MarkupSplitter.IsMarkupNode(new MarkupElementIntermediateNode()));
        Assert.True(MarkupSplitter.IsMarkupNode(new MarkupBlockIntermediateNode()));
        Assert.True(MarkupSplitter.IsMarkupNode(new HtmlContentIntermediateNode()));
    }

    [Fact]
    public void IsMarkupNode_ExcludesCSharpAndSurfaceNodes()
    {
        // Raw C# and structured/extension members are not routable markup -- crucially an @inject node,
        // which (like a template) is an ExtensionIntermediateNode but is surface, not markup.
        Assert.False(MarkupSplitter.IsMarkupNode(new CSharpCodeIntermediateNode()));
        Assert.False(MarkupSplitter.IsMarkupNode(new FieldDeclarationIntermediateNode { Name = "_f", Type = "int" }));
        Assert.False(MarkupSplitter.IsMarkupNode(new MethodDeclarationIntermediateNode()));
        Assert.False(MarkupSplitter.IsMarkupNode(
            new Components.ComponentInjectIntermediateNode("Foo", "Bar", typeSpan: null, memberSpan: null)));
    }

    [Fact]
    public void Split_ClassBodyWithInject_FallsBack()
    {
        // A component whose @code mixes markup with an @inject can't be split yet: the inject is surface
        // the splitter can't route, so the whole file falls back rather than mis-route it to impl.
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(
            new Components.ComponentInjectIntermediateNode("Foo", "Bar", typeSpan: null, memberSpan: null),
            CreateCSharpCode("void M(RenderTreeBuilder __builder) { "),
            new MarkupElementIntermediateNode { TagName = "ul" },
            CreateCSharpCode(" }"),
            renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, ParserOptions(LanguageVersion.CSharp13));

        var fallback = Assert.IsType<SplitDecision.SplitFallback>(decision);
        Assert.Equal(FallbackReason.UnsupportedClassBodyNode, fallback.Reason);
    }

    [Fact]
    public void Split_MarkupHelperMethod_ReturnsSplitPlanRoutedToImpl()
    {
        var renderMethod = CreateRenderMethod();
        var markup = new MarkupElementIntermediateNode { TagName = "ul" };
        var primaryClass = CreatePrimaryClass(
            CreateCSharpCode("void M(RenderTreeBuilder __builder) { "),
            markup,
            CreateCSharpCode(" }"),
            renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, ParserOptions(LanguageVersion.CSharp10));

        var plan = Assert.IsType<SplitDecision.SplitPlan>(decision);
        Assert.True(plan.RequiresSplit);
        var member = Assert.Single(plan.Members);
        Assert.Equal(MemberSplitKind.MarkupMethod, member.Kind);
        Assert.Empty(member.DeclPieces);

        // The whole method lifts to impl: its C# chunks stay by reference and the markup node is carried
        // through untouched.
        Assert.Equal(3, member.ImplPieces.Length);
        Assert.Same(markup, member.ImplPieces[1]);
    }

    [Fact]
    public void Split_ClassBodyWithDirective_FallsBack()
    {
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(
            CreateCSharpCode("#nullable enable\n public RenderFragment Header => "),
            new TemplateIntermediateNode(),
            CreateCSharpCode(";"),
            renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, ParserOptions(LanguageVersion.CSharp13));

        var fallback = Assert.IsType<SplitDecision.SplitFallback>(decision);
        Assert.Equal(FallbackReason.ClassBodyHasDirectives, fallback.Reason);
    }

    [Theory]
    [InlineData("#if DEBUG", true)]
    [InlineData("   #pragma warning disable", true)]     // leading whitespace before the directive
    [InlineData("int x = 1;\n#endif", true)]             // directive on a later line
    [InlineData("int x = 1;", false)]
    [InlineData("var s = \"not # a directive\";", false)] // hash mid-line is not a directive
    public void HasPreprocessorDirective_DetectsLineAnchoredHash(string text, bool expected)
    {
        Assert.Equal(expected, MarkupSplitter.HasPreprocessorDirective(text));
    }

    [Fact]
    public void Split_MarkupProperty_FallsBack()
    {
        // A property with markup can't stay in the markup-free decl half but must (it's descriptor
        // surface), so the whole file falls back -- on any language version.
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(
            CreateCSharpCode("[Parameter] public RenderFragment Header => "),
            new TemplateIntermediateNode(),
            CreateCSharpCode(";"),
            renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, ParserOptions(LanguageVersion.CSharp13));

        var fallback = Assert.IsType<SplitDecision.SplitFallback>(decision);
        Assert.Equal(FallbackReason.MarkupProperty, fallback.Reason);
    }

    [Fact]
    public void Split_MarkupFreeProperty_AlongsideMarkupMethod_StaysInDecl()
    {
        // A markup-free property is descriptor surface and stays in decl; the markup method that forces
        // the split lifts to impl.
        var renderMethod = CreateRenderMethod();
        var property = CreateCSharpCode("[Parameter] public int Count { get; set; } void M(RenderTreeBuilder __builder) { ");
        var primaryClass = CreatePrimaryClass(
            property,
            new MarkupElementIntermediateNode { TagName = "ul" },
            CreateCSharpCode(" }"),
            renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, ParserOptions(LanguageVersion.CSharp13));

        var plan = Assert.IsType<SplitDecision.SplitPlan>(decision);
        Assert.Equal(2, plan.Members.Length);
        Assert.Equal(MemberSplitKind.NoMarkup, plan.Members[0].Kind);
        Assert.Contains("Count", TokenText((CSharpCodeIntermediateNode)Assert.Single(plan.Members[0].DeclPieces)));
        Assert.Equal(MemberSplitKind.MarkupMethod, plan.Members[1].Kind);
        Assert.Empty(plan.Members[1].DeclPieces);
    }

    [Fact]
    public void Split_MarkupProperty_BelowCSharp13_AlsoFallsBack()
    {
        // The fallback is version-independent: no C# 13 gate anymore.
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(
            CreateCSharpCode("public RenderFragment Header => "),
            new TemplateIntermediateNode(),
            CreateCSharpCode(";"),
            renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, ParserOptions(LanguageVersion.CSharp10));

        var fallback = Assert.IsType<SplitDecision.SplitFallback>(decision);
        Assert.Equal(FallbackReason.MarkupProperty, fallback.Reason);
    }

    [Fact]
    public void Split_MarkupMethod_BelowCSharp13_StillSplits()
    {
        // Only markup properties fall back; a markup method lifts wholesale on any C#.
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(
            CreateCSharpCode("void M(RenderTreeBuilder __builder) { "),
            new MarkupElementIntermediateNode { TagName = "ul" },
            CreateCSharpCode(" }"),
            renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, ParserOptions(LanguageVersion.CSharp10));

        Assert.IsType<SplitDecision.SplitPlan>(decision);
    }

    [Fact]
    public void Split_UnrecoverableParse_FallsBack()
    {
        // An unmatched open brace before markup leaves the marker outside any recoverable member.
        var renderMethod = CreateRenderMethod();
        var primaryClass = CreatePrimaryClass(
            CreateCSharpCode("void M(RenderTreeBuilder __builder) { "),
            new MarkupElementIntermediateNode { TagName = "div" },
            renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, ParserOptions(LanguageVersion.CSharp13));

        var fallback = Assert.IsType<SplitDecision.SplitFallback>(decision);
        Assert.Equal(FallbackReason.UnrecoverableParse, fallback.Reason);
    }

    [Fact]
    public void BuildRoutedMembers_MixedMembers_SlicesChunkAndRoutesEachInOrder()
    {
        // One C# chunk holds a whole field plus the start of a markup method, so it must be sliced at
        // the member boundary and each slice routed to its own member. (A markup property never reaches
        // routing -- it takes the whole file to fallback -- so routing only ever sees NoMarkup and
        // MarkupMethod members.)
        var markup = new MarkupElementIntermediateNode { TagName = "div" };
        var analysis = MarkupSplitter.BuildAnalysisDocument([
            CreateCSharpCode("[Parameter] public int Count { get; set; } private void Helper(RenderTreeBuilder __builder) { "),
            markup,
            CreateCSharpCode(" }")]);
        var members = MarkupSplitter.ClassifyMembers(analysis, CSharpParseOptions.Default);
        Assert.NotNull(members);

        var routed = MarkupSplitter.BuildRoutedMembers(analysis, members.Value);

        Assert.Equal(2, routed.Length);

        // int Count -> decl only, one sliced C# piece.
        Assert.Equal(MemberSplitKind.NoMarkup, routed[0].Kind);
        Assert.Empty(routed[0].ImplPieces);
        var countPiece = Assert.IsType<CSharpCodeIntermediateNode>(Assert.Single(routed[0].DeclPieces));
        Assert.Contains("int Count", TokenText(countPiece));
        Assert.DoesNotContain("Helper", TokenText(countPiece));

        // Helper -> impl only, carrying the markup node by reference.
        Assert.Equal(MemberSplitKind.MarkupMethod, routed[1].Kind);
        Assert.Empty(routed[1].DeclPieces);
        Assert.Contains(routed[1].ImplPieces, p => ReferenceEquals(p, markup));
    }

    private static RazorParserOptions ParserOptions(LanguageVersion version)
        => RazorParserOptions.Default.WithCSharpParseOptions(
            CSharpParseOptions.Default.WithLanguageVersion(version));

    private static string TokenText(CSharpCodeIntermediateNode node)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in node.Children)
        {
            if (child is IntermediateToken token)
            {
                sb.Append(token.Content);
            }
        }

        return sb.ToString();
    }
}
