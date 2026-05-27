// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Microsoft.AspNetCore.Razor.Language;

public class RazorParserOptionsTest
{
    [Fact]
    public void Create_LatestVersion_AllowsLatestFeatures()
    {
        // Arrange & Act
        var builder = new RazorParserOptions.Builder(RazorLanguageVersion.Latest, RazorFileKind.Legacy);
        var options = builder.ToOptions();

        // Assert
        Assert.True(options.AllowComponentFileKind);
        Assert.True(options.AllowRazorInAllCodeBlocks);
        Assert.True(options.AllowUsingVariableDeclarations);
        Assert.True(options.AllowNullableForgivenessOperator);
    }

    [Fact]
    public void Create_21Version_Allows21Features()
    {
        // Arrange & Act
        var builder = new RazorParserOptions.Builder(RazorLanguageVersion.Version_2_1, RazorFileKind.Legacy);
        var options = builder.ToOptions();

        // Assert
        Assert.True(options.AllowMinimizedBooleanTagHelperAttributes);
        Assert.True(options.AllowHtmlCommentsInTagHelpers);
    }

    [Fact]
    public void Create_OldestVersion_DoesNotAllowLatestFeatures()
    {
        // Arrange & Act
        var builder = new RazorParserOptions.Builder(RazorLanguageVersion.Version_1_0, RazorFileKind.Legacy);
        var options = builder.ToOptions();

        // Assert
        Assert.False(options.AllowMinimizedBooleanTagHelperAttributes);
        Assert.False(options.AllowHtmlCommentsInTagHelpers);
        Assert.False(options.AllowComponentFileKind);
        Assert.False(options.AllowRazorInAllCodeBlocks);
        Assert.False(options.AllowUsingVariableDeclarations);
        Assert.False(options.AllowNullableForgivenessOperator);
    }

    [Fact]
    public void UseEnhancedRecovery_DefaultsToFalse()
    {
        // Arrange & Act
        var builder = new RazorParserOptions.Builder(RazorLanguageVersion.Latest, RazorFileKind.Legacy);
        var options = builder.ToOptions();

        // Assert
        Assert.False(options.UseEnhancedRecovery);
    }

    [Fact]
    public void UseEnhancedRecovery_RoundTripsThroughBuilder()
    {
        // Arrange
        var builder = new RazorParserOptions.Builder(RazorLanguageVersion.Latest, RazorFileKind.Legacy)
        {
            UseEnhancedRecovery = true,
        };

        // Act
        var options = builder.ToOptions();

        // Assert
        Assert.True(options.UseEnhancedRecovery);
    }

    [Fact]
    public void UseEnhancedRecovery_RoundTripsThroughWithFlags()
    {
        // Arrange
        var defaultOptions = RazorParserOptions.Default;
        Assert.False(defaultOptions.UseEnhancedRecovery);

        // Act
        var enhanced = defaultOptions.WithFlags(useEnhancedRecovery: true);

        // Assert
        Assert.True(enhanced.UseEnhancedRecovery);

        // Round-trip back off:
        var legacy = enhanced.WithFlags(useEnhancedRecovery: false);
        Assert.False(legacy.UseEnhancedRecovery);
    }
}
