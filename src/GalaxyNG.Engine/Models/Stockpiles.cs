namespace GalaxyNG.Engine.Models;

public record struct Stockpiles(double Capital, double Materials, double Colonists)
{
    public static readonly Stockpiles Empty = new(0, 0, 0);

    public Stockpiles Add(Stockpiles other) =>
        new(Capital + other.Capital, Materials + other.Materials, Colonists + other.Colonists);

    public Stockpiles WithCapital(double v)   => this with { Capital   = v };
    public Stockpiles WithMaterials(double v) => this with { Materials = v };
    public Stockpiles WithColonists(double v) => this with { Colonists = v };
}
