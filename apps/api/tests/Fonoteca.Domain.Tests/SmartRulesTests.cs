using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class SmartRulesTests
{
    [Fact]
    public void ANestedTreeOfGoodRulesHasNoProblems()
    {
        var criteria = new SmartCriteria(
            Group(
                SmartMatch.All,
                Condition(SmartField.Genre, SmartOperator.Is, "jazz"),
                Group(
                    SmartMatch.Any,
                    Condition(SmartField.Year, SmartOperator.Between, "1955", "1965"),
                    Condition(SmartField.HiRes, SmartOperator.IsTrue),
                    Condition(SmartField.Added, SmartOperator.InTheLast, "30"),
                    Condition(SmartField.Composer, SmartOperator.IsEmpty))),
            SmartSort.Year,
            Reverse: true,
            Limit: 100);

        Assert.Empty(SmartRules.Problems(criteria));
    }

    [Fact]
    public void AnEmptyTopGroupIsEverySong()
    {
        Assert.Empty(SmartRules.Problems(new SmartCriteria(Group(SmartMatch.All), SmartSort.Added, false, null)));
    }

    [Theory]
    [InlineData(SmartField.Title, SmartOperator.AtLeast, "3")]
    [InlineData(SmartField.Year, SmartOperator.Contains, "19")]
    [InlineData(SmartField.Lossless, SmartOperator.Is, "true")]
    [InlineData(SmartField.Added, SmartOperator.Is, "3")]
    [InlineData(SmartField.Year, SmartOperator.Is, "nineteen")]
    [InlineData(SmartField.Year, SmartOperator.Between, "1955")]
    [InlineData(SmartField.Added, SmartOperator.InTheLast, "1.5")]
    [InlineData(SmartField.Added, SmartOperator.InTheLast, "0")]
    [InlineData(SmartField.Genre, SmartOperator.Is, "  ")]
    public void AConditionTheFieldCannotAnswerIsAProblem(SmartField field, SmartOperator op, string value)
    {
        var criteria = new SmartCriteria(Group(SmartMatch.All, Condition(field, op, value)), SmartSort.Added, false, null);

        Assert.Single(SmartRules.Problems(criteria));
    }

    [Fact]
    public void TheShapeOfTheTreeIsChecked()
    {
        Assert.NotEmpty(SmartRules.Problems(null));
        Assert.NotEmpty(SmartRules.Problems(new SmartCriteria(Condition(SmartField.Title, SmartOperator.Is, "x"), SmartSort.Added, false, null)));
        Assert.NotEmpty(SmartRules.Problems(new SmartCriteria(
            Group(SmartMatch.All, new SmartRule(null, [], SmartField.Title, SmartOperator.Is, "x", null)),
            SmartSort.Added,
            false,
            null)));
        Assert.NotEmpty(SmartRules.Problems(new SmartCriteria(Group(SmartMatch.All), SmartSort.Added, false, 0)));

        var deep = Group(SmartMatch.All);
        for (var level = 0; level < SmartRules.MaxDepth; level++) deep = Group(SmartMatch.Any, deep);

        Assert.NotEmpty(SmartRules.Problems(new SmartCriteria(deep, SmartSort.Added, false, null)));
    }

    [Fact]
    public void EveryFieldHasAKindAndEveryKindItsOperators()
    {
        foreach (var field in Enum.GetValues<SmartField>())
        {
            Assert.Contains(Enum.GetValues<SmartOperator>(), op => SmartRules.Allows(SmartRules.KindOf(field), op));
        }
    }

    [Fact]
    public void RulesReadBackAsTheyWereSaved()
    {
        var criteria = new SmartCriteria(
            Group(SmartMatch.Any, Condition(SmartField.Artist, SmartOperator.Contains, "Davis"), Condition(SmartField.Lossless, SmartOperator.IsFalse)),
            SmartSort.Random,
            false,
            25);

        var json = SmartRules.Serialize(criteria);
        var read = SmartRules.Parse(json)!;

        Assert.Contains("\"field\":\"Artist\"", json, StringComparison.Ordinal);
        Assert.Equal(json, SmartRules.Serialize(read));
        Assert.Null(SmartRules.Parse("{not json"));
    }

    private static SmartRule Group(SmartMatch match, params SmartRule[] rules) => new(match, rules, null, null, null, null);

    private static SmartRule Condition(SmartField field, SmartOperator op, string? value = null, string? to = null) =>
        new(null, null, field, op, value, to);
}
