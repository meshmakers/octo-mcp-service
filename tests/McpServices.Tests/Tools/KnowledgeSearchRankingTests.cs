using System.Text;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#9165: the knowledge_search ranking and the no-answer hint against the knowledge pages of the eval tenant (the
///     builder blueprint pages plus the eval corpus, both installed on m1-e2e) and two forms of every eval question: the
///     queries the model sent in the live knowledge-query eval 1.1.2, and the whole question text, as a client that does
///     not rephrase (Studio Assistant, other agents) sends it. Fixture <c>Fixtures/knowledge-search-eval-1.1.2.json</c>,
///     provenance in its description. The tool ranks pages with <see cref="KnowledgeTools.Match" />, lists the query words
///     no page contains and counts them with <see cref="KnowledgeTools.HintTerms" />; these tests apply the same steps.
/// </summary>
public class KnowledgeSearchRankingTests
{
    private static readonly Lazy<Fixture> Data = new(LoadFixture);

    public static TheoryData<string, string> AnswerableForms() => Forms(answerable: true);

    public static TheoryData<string, string> NoAnswerQuestionTexts()
    {
        var data = new TheoryData<string, string>();
        foreach (var q in Data.Value.Questions.Where(q => !q.Answerable))
        {
            data.Add(q.Id, q.Query);
        }

        return data;
    }

    [Fact]
    public void HttpApiQuestion_RanksTheHttpApiPipelinePageInTheTop3()
    {
        Rank("build an HTTP API for my app").Take(3).Should().Contain("app-http-api-pipelines");
    }

    [Fact]
    public void HttpApiQuestion_WithTheFieldScoreAlone_RanksTheHttpApiPipelinePageBelowTheTop3()
    {
        // The finding of the live eval (finding 3): title, summary and section hits alone let the broad pages win, even
        // with the function words already left out of the query words.
        var terms = KnowledgeTools.Terms("build an HTTP API for my app");
        var ranked = Data.Value.Pages
            .Select(p => (p.Content.Title, p.Slug, Score: KnowledgeTools.Score(p.Content, terms)))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Slug)
            .ToList();

        ranked.Take(3).Should().NotContain("app-http-api-pipelines");
    }

    [Theory]
    [MemberData(nameof(AnswerableForms))]
    public void AnswerableQuestion_FindsAnExpectedPageInTheTop3(string id, string query)
    {
        var expected = Data.Value.Questions.First(q => q.Id == id).Expected;

        Rank(query).Take(3).Should().IntersectWith(expected, $"{id} expects one of {string.Join(", ", expected)}");
    }

    [Theory]
    [MemberData(nameof(AnswerableForms))]
    public void AnswerableQuestion_GetsNoNoAnswerHint(string id, string query)
    {
        HintTerms(query).Should().BeEmpty($"{id} is answerable and must not get the no-answer hint");
    }

    [Fact]
    public void AllAnswerableQuestions_InBothForms_GetZeroNoAnswerHints()
    {
        var forms = Data.Value.Queries.Concat(Data.Value.Questions).Where(q => q.Answerable).ToList();

        var hinted = forms.Where(q => HintTerms(q.Query).Count > 0).Select(q => $"{q.Id}: {q.Query}").ToList();

        forms.Should().HaveCount(38, "19 recorded queries and 19 question texts");
        hinted.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(NoAnswerQuestionTexts))]
    public void NoAnswerQuestionText_GetsTheNoAnswerHint(string id, string question)
    {
        HintTerms(question).Should().NotBeEmpty($"{id} has no answer on any page");
    }

    [Fact]
    public void NoAnswerRecordedQueries_GetTheHint_ExceptTheOneWithoutAMissingWord()
    {
        var forms = Data.Value.Queries.Where(q => !q.Answerable).ToList();

        var missed = forms.Where(q => HintTerms(q.Query).Count == 0).Select(q => q.Query).ToList();

        // Known limit: every word of "Refinery Studio Wi-Fi network" (q23) is on some page, so no word-based signal sees
        // that the question is about a laptop's Wi-Fi.
        forms.Should().HaveCount(9);
        missed.Should().Equal("Refinery Studio Wi-Fi network");
    }

    [Theory]
    [InlineData("roll out deploy app AWS Lambda function", new[] { "aws", "lambda" })]
    [InlineData("How do I roll out my OctoMesh app to an AWS Lambda function?", new[] { "aws", "lambda" })]
    [InlineData("How do I publish my OctoMesh app in the Apple App Store?", new[] { "apple" })]
    [InlineData("Why does my laptop's Wi-Fi drop when I open Refinery Studio?", new[] { "laptop" })]
    public void NoAnswerQuestion_NamesTheWordsItIsAbout(string query, string[] expected)
    {
        var hintTerms = HintTerms(query);

        hintTerms.Should().Contain(expected);
        KnowledgeTools.NoAnswerHint(3, hintTerms).Should().Contain(KnowledgeConventions.NoAnswerSentence);
    }

    [Theory]
    [InlineData("deploy a pipline with a ForEach")] // pipeline
    [InlineData("install a blueprnt on a tenent")] // blueprint, tenant
    public void ATypoOfAPageWord_GetsNoNoAnswerHint(string query)
    {
        HintTerms(query).Should().BeEmpty();
    }

    private static List<string> Rank(string query)
    {
        var terms = KnowledgeTools.Terms(query);
        return Data.Value.Pages
            .Select(p => (p.Content.Title, p.Slug, KnowledgeTools.Match(p.Content, p.Slug, terms).Score))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Slug)
            .ToList();
    }

    // Like knowledge_search: the query words no page contains, counted for the hint.
    private static List<string> HintTerms(string query)
    {
        var terms = KnowledgeTools.Terms(query);
        var matched = Data.Value.Pages
            .SelectMany(p => KnowledgeTools.Match(p.Content, p.Slug, terms).MatchedTerms)
            .ToHashSet();
        return KnowledgeTools.HintTerms(terms.Where(t => !matched.Contains(t)),
            Data.Value.Pages.SelectMany(p => KnowledgeTools.PageWords(p.Content, p.Slug)));
    }

    private static TheoryData<string, string> Forms(bool answerable)
    {
        var data = new TheoryData<string, string>();
        foreach (var q in Data.Value.Queries.Concat(Data.Value.Questions).Where(q => q.Answerable == answerable))
        {
            data.Add(q.Id, q.Query);
        }

        return data;
    }

    private static Fixture LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tools", "Fixtures", "knowledge-search-eval-1.1.2.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var pages = root.GetProperty("pages").EnumerateObject()
            .SelectMany(set => set.Value.EnumerateArray().Select(ReadPage))
            .ToList();
        return new Fixture(pages, ReadQuestions(root, "queries", "query"), ReadQuestions(root, "questions", "question"));
    }

    private static List<EvalQuestion> ReadQuestions(JsonElement root, string list, string textField) => root
        .GetProperty(list).EnumerateArray().Select(q => new EvalQuestion(
            q.GetProperty("id").GetString()!,
            q.GetProperty("answerable").GetBoolean(),
            q.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!).ToList(),
            q.GetProperty(textField).GetString()!))
        .ToList();

    // The fixture stores section text as word:count; repeating each word restores the hits the score counts.
    private static FixturePage ReadPage(JsonElement page)
    {
        var body = new StringBuilder();
        foreach (var entry in page.GetProperty("body").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = entry.LastIndexOf(':');
            var count = int.Parse(entry[(colon + 1)..]);
            for (var i = 0; i < count; i++)
            {
                body.Append(entry[..colon]).Append(' ');
            }
        }

        var content = new KnowledgeContent(page.GetProperty("title").GetString()!, 4,
            page.GetProperty("summary").GetString(),
            [new KnowledgeSectionDto { Heading = string.Empty, SectionContent = body.ToString() }], [], [], 0);
        return new FixturePage(page.GetProperty("slug").GetString()!, content);
    }

    private sealed record FixturePage(string Slug, KnowledgeContent Content);

    // A recorded query or a question text; Query holds the text that is sent.
    private sealed record EvalQuestion(string Id, bool Answerable, List<string> Expected, string Query);

    private sealed record Fixture(List<FixturePage> Pages, List<EvalQuestion> Queries, List<EvalQuestion> Questions);
}
