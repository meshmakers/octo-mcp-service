using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Xunit;

namespace McpServices.Tests.Services.Knowledge;

/// <summary>AB#8566: inflected forms share a stem, so knowledge_search scores what the stemming text index matches.</summary>
public class KnowledgeStemmerTests
{
    [Theory]
    [InlineData("pipeline", "pipelines")]
    [InlineData("policy", "policies")]
    [InlineData("box", "boxes")]
    [InlineData("match", "matches")]
    [InlineData("deploy", "deploys")]
    [InlineData("deploy", "deployed")]
    [InlineData("deploy", "deploying")]
    [InlineData("run", "running")]
    [InlineData("run", "runs")]
    [InlineData("configure", "configured")]
    [InlineData("configure", "configuring")]
    [InlineData("need", "needs")]
    [InlineData("need", "needed")]
    [InlineData("agree", "agreed")]
    [InlineData("speed", "speeds")]
    [InlineData("use", "uses")]
    [InlineData("use", "used")]
    [InlineData("use", "using")]
    [InlineData("age", "aged")]
    [InlineData("owe", "owed")]
    public void InflectedForms_ShareTheStem(string word, string inflected)
    {
        KnowledgeStemmer.Stem(inflected).Should().Be(KnowledgeStemmer.Stem(word));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("analysis")]
    [InlineData("class")]
    [InlineData("string")]
    [InlineData("thing")]
    [InlineData("bring")]
    [InlineData("sing")]
    public void WordsThatOnlyLookInflected_KeepTheirStem(string word)
    {
        KnowledgeStemmer.Stem(word).Should().Be(word);
    }

    [Theory]
    [InlineData("pipeline", "schema")]
    [InlineData("deploy", "delete")]
    [InlineData("run", "rule")]
    public void DifferentWords_HaveDifferentStems(string a, string b)
    {
        KnowledgeStemmer.Stem(a).Should().NotBe(KnowledgeStemmer.Stem(b));
    }

    [Fact]
    public void Words_SplitsOnPunctuationAndHyphens_AndLowersCase()
    {
        KnowledgeStemmer.Words("Die Zebrafisch-Pipeline, läuft 2x nachts.").Should()
            .Equal("die", "zebrafisch", "pipeline", "läuft", "2x", "nachts");
    }
}
