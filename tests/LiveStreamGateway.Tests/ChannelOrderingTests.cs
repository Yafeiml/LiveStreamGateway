namespace LiveStreamGateway.Tests;

public class ChannelOrderingTests
{
    [Fact]
    public void CompleteOrder_ReordersExistingChannelInstances()
    {
        var first = Channel("one");
        var second = Channel("two");
        var third = Channel("three");

        bool success = ChannelOrdering.TryBuild(
            [first, second, third],
            ["three", "one", "two"],
            out List<ChannelConfig> reordered,
            out string error);

        Assert.True(success, error);
        Assert.Collection(
            reordered,
            item => Assert.Same(third, item),
            item => Assert.Same(first, item),
            item => Assert.Same(second, item));
    }

    [Fact]
    public void MissingChannel_IsRejected()
    {
        bool success = ChannelOrdering.TryBuild(
            [Channel("one"), Channel("two")],
            ["one"],
            out List<ChannelConfig> reordered,
            out string error);

        Assert.False(success);
        Assert.Empty(reordered);
        Assert.Contains("全部频道", error);
    }

    [Fact]
    public void DuplicateChannel_IsRejected()
    {
        bool success = ChannelOrdering.TryBuild(
            [Channel("one"), Channel("two")],
            ["one", "one"],
            out List<ChannelConfig> reordered,
            out string error);

        Assert.False(success);
        Assert.Empty(reordered);
        Assert.Contains("重复", error);
    }

    [Fact]
    public void UnknownChannel_IsRejected()
    {
        bool success = ChannelOrdering.TryBuild(
            [Channel("one"), Channel("two")],
            ["one", "unknown"],
            out List<ChannelConfig> reordered,
            out string error);

        Assert.False(success);
        Assert.Empty(reordered);
        Assert.Contains("不存在", error);
    }

    private static ChannelConfig Channel(string id) => new()
    {
        Id = id,
        Name = id,
        Platform = "huya",
        Url = $"https://www.huya.com/{id}",
        Quality = "OD"
    };
}
