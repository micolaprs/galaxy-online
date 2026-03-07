using FluentAssertions;
using GalaxyNG.Engine.Models;
using GalaxyNG.Engine.Services;

namespace GalaxyNG.Engine.Tests;

public sealed class OrderParserTests
{
    private readonly OrderParser _parser = new();

    [Fact]
    public void Parse_design_ship_order()
    {
        var (orders, errors) = _parser.Parse("d Fighter 2.48 1 1.20 1.27 0");
        errors.Should().BeEmpty();
        orders.Should().ContainSingle(o => o.Kind == OrderKind.DesignShip);
        orders[0].Args[0].Should().Be("Fighter");
    }

    [Fact]
    public void Parse_set_production()
    {
        var (orders, _) = _parser.Parse("p Home CAP");
        orders.Should().ContainSingle(o => o.Kind == OrderKind.SetProduction);
        orders[0].Args.Should().Equal(["Home", "CAP"]);
    }

    [Fact]
    public void Parse_send_group()
    {
        var (orders, _) = _parser.Parse("s 1 Alpha");
        orders.Should().ContainSingle(o => o.Kind == OrderKind.SendGroup);
        orders[0].Args[0].Should().Be("1");
    }

    [Fact]
    public void Parse_send_fleet()
    {
        var (orders, _) = _parser.Parse("s MyFleet Alpha");
        orders.Should().ContainSingle(o => o.Kind == OrderKind.SendFleet);
    }

    [Fact]
    public void Comments_are_stripped()
    {
        var (orders, errors) = _parser.Parse("p Home CAP ; this is a comment");
        errors.Should().BeEmpty();
        orders.Should().ContainSingle(o => o.Kind == OrderKind.SetProduction);
    }

    [Fact]
    public void Parse_diplomacy_orders()
    {
        var text = """
            a Aliens
            w Enemies
            """;
        var (orders, _) = _parser.Parse(text);
        orders.Should().Contain(o => o.Kind == OrderKind.DeclareAlliance);
        orders.Should().Contain(o => o.Kind == OrderKind.DeclareWar);
    }

    [Fact]
    public void Parse_multiple_orders()
    {
        var text = """
            d Scout 1 0 0 0 0
            p Home Scout
            s 1 Alpha
            l 2 COL
            """;
        var (orders, errors) = _parser.Parse(text);
        errors.Should().BeEmpty();
        orders.Should().HaveCount(4);
    }

    [Fact]
    public void Missing_args_produces_error()
    {
        var (_, errors) = _parser.Parse("d ShipName");  // needs 7 tokens
        errors.Should().NotBeEmpty();
    }
}
