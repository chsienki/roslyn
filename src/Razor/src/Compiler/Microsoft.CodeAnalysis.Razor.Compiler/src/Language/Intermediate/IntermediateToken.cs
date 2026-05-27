// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Razor.Language.Intermediate;

public abstract class IntermediateToken : IntermediateNode
{
    public bool IsLazy { get; }

    /// <summary>
    /// True when this token was synthesized to represent a missing C# value
    /// (e.g., the empty value of <c>@onclick=""</c>) rather than user-written
    /// content. Downstream codegen emits a safe placeholder (e.g.,
    /// <c>default!</c>) instead of leaving a hole that produces CS1525 in the
    /// generated C#.
    /// </summary>
    internal bool IsMissingValue { get; set; }

    private object _content;

    public string Content
        => _content is LazyContent lazy ? lazy.Value : (string)_content;

    public override IntermediateNodeCollection Children => IntermediateNodeCollection.ReadOnly;

    protected IntermediateToken(string content, SourceSpan? source)
    {
        _content = content;
        IsLazy = false;

        if (source != null)
        {
            Source = source;
        }
    }

    private protected IntermediateToken(LazyContent content, SourceSpan? source)
    {
        _content = content;
        IsLazy = true;

        if (source != null)
        {
            Source = source;
        }
    }

    public void UpdateContent(string content)
    {
        _content = content;
    }

    public override void Accept(IntermediateNodeVisitor visitor)
        => visitor.VisitToken(this);

    public override void FormatNode(IntermediateNodeFormatter formatter)
    {
        formatter.WriteContent(Content);

        formatter.WriteProperty(nameof(Content), Content);
    }
}
