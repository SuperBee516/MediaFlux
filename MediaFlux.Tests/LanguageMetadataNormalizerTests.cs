using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LanguageMetadataNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("UND", "")]
    [InlineData(" ENG ", "eng")]
    [InlineData("Jpn", "jpn")]
    [InlineData("x-private", "x-private")]
    public void NormalizeUsesOneUnspecifiedValueAndPreservesOtherTags(string? value, string expected) =>
        Assert.Equal(expected, LanguageMetadataNormalizer.Normalize(value));
}
