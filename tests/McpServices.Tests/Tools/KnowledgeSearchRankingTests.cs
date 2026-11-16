using System.Text;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#9165: the knowledge_search ranking and the no-answer hint against the real knowledge pages of the builder
///     blueprint and every query the model sent in the live knowledge-query eval 1.1.2 (fixture
///     <c>Fixtures/knowledge-search-eval-1.1.2.json</c>, provenance in its description). The tool ranks pages with
///     <see cref="KnowledgeTools.Match" /> and lists the query words no match contains as unmatched terms; these tests
///     apply the same two steps to the fixture pages.
/// </summary>
public class KnowledgeSearchRankingTests
{
    private static readonly Lazy<Fixture> Data = new(LoadFixture);

    public static TheoryData<string, string> AnswerableQueries() => Queries(answerable: true);

    public static TheoryData<string, string> NoAnswerQueries() => Queries(answerable: false);

    [Fact]
    public void HttpApiQuestion_RanksTheHttpApiPipelinePageInTheTop3()
    {
        var ranked = Rank("builder", "build an HTTP API for my app");

        ranked.Take(3).Select(r => r.Slug).Should().Contain("app-http-api-pipelines");
    }

    [Fact]
    public void HttpApiQuestion_WithTheOldFieldScoreAlone_RankedTheHttpApiPipelinePageBelowTheTop3()
    {
        // The finding of the live eval (finding 3): title, summary and section hits alone let the broad pages win.
        var terms = KnowledgeTools.Terms("build an HTTP API for my app");
        var ranked = Data.Value.Pages["builder"]
            .Select(p => (p.Slug, Score: KnowledgeTools.Score(p.Content, terms)))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Select(r => r.Slug)
            .ToList();

        ranked.Take(3).Should().NotContain("app-http-api-pipelines");
    }

    [Theory]
    [MemberData(nameof(AnswerableQueries))]
    public void AnswerableRecordedQuery_FindsAnExpectedPageInTheTop3(string id, string query)
    {
        var question = Data.Value.Queries.First(q => q.Id == id && q.Query == query);

        Rank(question.Set, query).Take(3).Select(r => r.Slug).Should().IntersectWith(question.Expected,
            $"{id} expects one of {string.Join(", ", question.Expected)}");
    }

    [Theory]
    [MemberData(nameof(AnswerableQueries))]
    public void AnswerableRecordedQuery_GetsNoNoAnswerHint(string id, string query)
    {
        var question = Data.Value.Queries.First(q => q.Id == id && q.Query == query);

        Unmatched(question.Set, query).Count.Should().BeLessThan(KnowledgeTools.NoAnswerHintMinUnmatchedTerms,
            $"{id}: an answerable question must not get the no-answer hint");
    }

    [Fact]
    public void AllAnswerableRecordedQueries_GetZeroNoAnswerHints()
    {
        var answerable = Data.Value.Queries.Where(q => q.Answerable).ToList();

        var hinted = answerable
            .Where(q => Unmatched(q.Set, q.Query).Count >= KnowledgeTools.NoAnswerHintMinUnmatchedTerms)
            .Select(q => q.Id)
            .ToList();

        answerable.Should().HaveCount(19);
        hinted.Should().BeEmpty();
    }

    [Theory]
    [InlineData("roll out deploy app AWS Lambda function")]
    [InlineData("How do I deploy an OctoMesh app as an AWS Lambda function?")]
    public void AwsLambdaQuestion_LeavesAwsAndLambdaUnmatched_AndGetsTheNoAnswerHint(string query)
    {
        var unmatched = Unmatched("builder", query);

        unmatched.Should().Contain(["aws", "lambda"]);
        unmatched.Count.Should().BeGreaterThanOrEqualTo(KnowledgeTools.NoAnswerHintMinUnmatchedTerms);
        KnowledgeTools.NoAnswerHint(3, unmatched).Should().Contain(KnowledgeConventions.NoAnswerSentence)
            .And.Contain("'aws', 'lambda'");
    }

    [Theory]
    [MemberData(nameof(NoAnswerQueries))]
    public void NoAnswerRecordedQuery_LeavesTheWordsItIsAboutUnmatched(string id, string query)
    {
        // The subject of every no-answer question (VAT, Apple, payment, laptop, ...) is on no page. The hint fires from
        // two such words on; a single one (q12 "apple", q23 "laptop") stays without it by design.
        var question = Data.Value.Queries.First(q => q.Id == id && q.Query == query);
        var subjects = new Dictionary<string, string[]>
        {
            ["q6-no-answer"] = ["poland"],
            ["q12-builder-no-answer"] = ["apple"],
            ["q17-admin-no-answer"] = ["payment"],
            ["q20-deploy-no-answer"] = ["aws", "lambda"],
            // "laptop" in one query, none in "Refinery Studio Wi-Fi network": Wi-Fi and network are on pages.
            ["q23-diagnose-no-answer"] = query.Contains("laptop", StringComparison.OrdinalIgnoreCase) ? ["laptop"] : []
        };

        var unmatched = Unmatched(question.Set, query);
        foreach (var subject in subjects[id])
        {
            unmatched.Should().Contain(subject);
        }
    }

    [Fact]
    public void Q20RecordedQuery_GetsTheNoAnswerHint()
    {
        var q20 = Data.Value.Queries.Where(q => q.Id == "q20-deploy-no-answer").ToList();

        q20.Should().NotBeEmpty();
        q20.Should().OnlyContain(q =>
            Unmatched(q.Set, q.Query).Count >= KnowledgeTools.NoAnswerHintMinUnmatchedTerms);
    }

    private static List<(string Slug, double Score)> Rank(string set, string query)
    {
        var terms = KnowledgeTools.Terms(query);
        return Data.Value.Pages[set]
            .Select(p => (p.Slug, KnowledgeTools.Match(p.Content, p.Slug, terms).Score))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score).ThenBy(r => r.Slug, StringComparer.Ordinal)
            .ToList();
    }

    // Like knowledge_search: the query words that no matching page contains.
    private static List<string> Unmatched(string set, string query)
    {
        var terms = KnowledgeTools.Terms(query);
        var matched = Data.Value.Pages[set]
            .SelectMany(p => KnowledgeTools.Match(p.Content, p.Slug, terms).MatchedTerms)
            .ToHashSet();
        return terms.Where(t => !matched.Contains(t)).ToList();
    }

    private static TheoryData<string, string> Queries(bool answerable)
    {
        var data = new TheoryData<string, string>();
        foreach (var q in Data.Value.Queries.Where(q => q.Answerable == answerable))
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
        var pages = root.GetProperty("pages").EnumerateObject().ToDictionary(set => set.Name,
            set => set.Value.EnumerateArray().Select(ReadPage).ToList());
        var queries = root.GetProperty("queries").EnumerateArray().Select(q => new RecordedQuery(
            q.GetProperty("id").GetString()!,
            q.GetProperty("set").GetString()!,
            q.GetProperty("answerable").GetBoolean(),
            q.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!).ToList(),
            q.GetProperty("query").GetString()!)).ToList();
        return new Fixture(pages, queries);
    }

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

    private sealed record RecordedQuery(string Id, string Set, bool Answerable, List<string> Expected, string Query);

    private sealed record Fixture(Dictionary<string, List<FixturePage>> Pages, List<RecordedQuery> Queries);
}
