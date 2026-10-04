using GmailOrganiser.Rules;
using GmailOrganiser.Rules.Review;

namespace GmailOrganiser.Tests.Unit.Rules;

public sealed class SenderIndexTests
{
    private static readonly (string, int)[] Senders =
    [
        ("one@example.com", 3),
        ("One@Example.com", 1),
        ("two@news.example.com", 5),
        ("three@notexample.com", 7),
        ("four@example.org", 11),
    ];

    private static readonly SenderIndex Index = SenderIndex.Build(Senders);

    [Theory]
    [InlineData(new[] { "one@example.com" }, 4)]
    [InlineData(new[] { "@example.com" }, 9)]
    [InlineData(new[] { "@news.example.com" }, 5)]
    [InlineData(new[] { "@com" }, 16)]
    [InlineData(new[] { "@example.com", "one@example.com", "@news.example.com" }, 9)]
    [InlineData(new[] { "one@example.com", "four@example.org", "one@example.com" }, 15)]
    [InlineData(new[] { "nobody@example.com", "@example.net" }, 0)]
    public void Count_matches_every_term_once_like_gmail(string[] terms, int expected) => Index.Count(terms).ShouldBe(expected);

    [Fact]
    public void Count_agrees_with_the_per_sender_term_match()
    {
        string[][] cases = [["@example.com"], ["@news.example.com", "four@example.org"], ["@org", "@example.com"]];
        foreach (var terms in cases)
        {
            var expected = Senders.Where(s => terms.Any(t => FilterCriteriaMapping.FromTermMatches(t, s.Item1.ToLowerInvariant())))
                .Sum(s => s.Item2);
            Index.Count(terms).ShouldBe(expected);
        }
    }
}
