using GalaxyNG.Engine.Models;
using static GalaxyNG.Engine.Constants;

namespace GalaxyNG.Engine.Services;

public sealed class ProductionEngine
{
    public void RunProduction(Game game)
    {
        foreach (var planet in game.Planets.Values.Where(p => p.IsOwned))
        {
            if (!game.Players.TryGetValue(planet.OwnerId!, out var owner))
            {
                continue;
            }

            ProducePlanet(planet, owner, game);
        }
    }

    private static void ProducePlanet(Planet planet, Player owner, Game game)
    {
        double prod = planet.Production + planet.ExcessProd;

        switch (planet.Producing)
        {
            case ProductionType.Capital:
                // Need 1 material per 5 prod (cost: 5 prod + 1 mat per cap)
                double maxCapByMat = planet.Stockpiles.Materials;
                double maxCapByProd = prod / CapitalProdCost;
                double capPossible = Math.Min(maxCapByMat, maxCapByProd);

                if (capPossible < 1 && planet.Stockpiles.Materials < 1)
                {
                    // Not enough materials — divert to materials production
                    planet.Stockpiles = planet.Stockpiles.WithMaterials(
                        planet.Stockpiles.Materials + prod * planet.Resources);
                    planet.ExcessProd = 0;
                    return;
                }

                double capProduced = Math.Floor(capPossible);
                double prodUsed = capProduced * CapitalProdCost;
                double matUsed = capProduced * CapitalMatCost;

                planet.Stockpiles = planet.Stockpiles
                    .WithCapital(planet.Stockpiles.Capital + capProduced)
                    .WithMaterials(planet.Stockpiles.Materials - matUsed);
                planet.ExcessProd = prod - prodUsed;
                break;

            case ProductionType.Materials:
                planet.Stockpiles = planet.Stockpiles.WithMaterials(
                    planet.Stockpiles.Materials + prod * planet.Resources);
                planet.ExcessProd = 0;
                break;

            case ProductionType.Drive:
                owner.DriveResearch = Research(prod, ResearchDriveCost, owner.DriveResearch,
                    owner.Tech.Drive, out var newDrive);
                owner.Tech = owner.Tech with { Drive = newDrive };
                planet.ExcessProd = 0;
                break;

            case ProductionType.Weapons:
                owner.WeaponsResearch = Research(prod, ResearchWeaponsCost, owner.WeaponsResearch,
                    owner.Tech.Weapons, out var newWeapons);
                owner.Tech = owner.Tech with { Weapons = newWeapons };
                planet.ExcessProd = 0;
                break;

            case ProductionType.Shields:
                owner.ShieldsResearch = Research(prod, ResearchShieldsCost, owner.ShieldsResearch,
                    owner.Tech.Shields, out var newShields);
                owner.Tech = owner.Tech with { Shields = newShields };
                planet.ExcessProd = 0;
                break;

            case ProductionType.Cargo:
                owner.CargoResearch = Research(prod, ResearchCargoCost, owner.CargoResearch,
                    owner.Tech.Cargo, out var newCargo);
                owner.Tech = owner.Tech with { Cargo = newCargo };
                planet.ExcessProd = 0;
                break;

            case ProductionType.Ship when planet.ShipTypeName is not null:
                BuildShips(planet, owner, prod);
                break;

            default:
                planet.ExcessProd = 0;
                break;
        }
    }

    /// <summary>
    /// Adds production to research accumulator and computes tech gain.
    /// Returns new accumulator value; sets newTech via out param.
    /// </summary>
    private static double Research(double prod, double costPerPoint,
        double accum, double currentTech, out double newTech)
    {
        accum += prod;
        newTech = currentTech;
        if (accum >= costPerPoint)
        {
            double gained = Math.Truncate(accum / costPerPoint);
            accum -= gained * costPerPoint;
            newTech += gained;
        }
        return accum;
    }

    private static void BuildShips(Planet planet, Player owner, double prod)
    {
        if (!owner.ShipTypes.TryGetValue(planet.ShipTypeName!, out var st))
        {
            return;
        }

        double costPerShip = st.Mass * ShipProdPerMass;
        double matPerShip = st.Mass * ShipMatPerMass;

        // How many ships can we afford?
        double byProd = prod / costPerShip;
        double byMat = planet.Stockpiles.Materials / matPerShip;

        // Need materials; if insufficient, divert remaining prod to material first
        if (planet.Stockpiles.Materials < matPerShip)
        {
            planet.Stockpiles = planet.Stockpiles.WithMaterials(
                planet.Stockpiles.Materials + prod * planet.Resources);
            planet.ExcessProd = 0;
            return;
        }

        int ships = (int)Math.Min(byProd, byMat);
        if (ships < 1)
        {
            planet.ExcessProd = prod;  // partial progress
            return;
        }

        double prodUsed = ships * costPerShip;
        double matUsed = ships * matPerShip;

        planet.Stockpiles = planet.Stockpiles.WithMaterials(planet.Stockpiles.Materials - matUsed);
        planet.ExcessProd = prod - prodUsed;

        // Add to existing group at planet or create new one
        var existingGroup = owner.Groups.FirstOrDefault(g =>
            g.At == planet.Name &&
            g.ShipTypeName == planet.ShipTypeName &&
            !g.InHyperspace &&
            g.Tech == owner.Tech &&
            g.CargoType is null);

        if (existingGroup is not null)
        {
            existingGroup.Ships += ships;
        }
        else
        {
            owner.Groups.Add(new Group
            {
                Number = owner.NextGroupNumber(),
                ShipTypeName = planet.ShipTypeName!,
                Ships = ships,
                Tech = owner.Tech,
                At = planet.Name,
            });
        }
    }

    public void GrowPopulation(Game game)
    {
        foreach (var planet in game.Planets.Values.Where(p => p.IsOwned && p.Population > 0))
        {
            double growth = planet.Population * PopGrowthRate;
            double newPop = planet.Population + growth;
            double excess = Math.Max(0, newPop - planet.Size);

            planet.Population = Math.Min(newPop, planet.Size);

            // Excess population → colonists  (8 pop = 1 colonist unit)
            if (excess > 0)
            {
                double colonists = excess / ColonistRatio;
                planet.Stockpiles = planet.Stockpiles.WithColonists(
                    planet.Stockpiles.Colonists + colonists);
            }

            // Industry can only grow via capital investment (production → industry handled elsewhere)
            // Ensure industry never exceeds population
            planet.Industry = Math.Min(planet.Industry, planet.Population);
        }
    }

    /// <summary>Invest capital into industry — converts capital stockpile to industry.</summary>
    public void InvestCapital(Game game)
    {
        foreach (var planet in game.Planets.Values.Where(p => p.IsOwned))
        {
            // Standard rule: all capital on planet used to grow industry
            double cap = planet.Stockpiles.Capital;
            if (cap <= 0)
            {
                continue;
            }

            double freeSlots = planet.Size - planet.Industry;
            double invest = Math.Min(cap, freeSlots);
            planet.Industry += invest;
            planet.Stockpiles = planet.Stockpiles.WithCapital(cap - invest);
        }
    }
}
