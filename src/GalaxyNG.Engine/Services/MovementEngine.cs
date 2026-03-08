using GalaxyNG.Engine.Models;

namespace GalaxyNG.Engine.Services;

public sealed class MovementEngine
{
    /// <summary>Move all groups through hyperspace by one turn.</summary>
    public void MoveGroups(Game game)
    {
        foreach (var player in game.Players.Values)
        foreach (var group in player.Groups.Where(g => g.InHyperspace))
        {
            if (!player.ShipTypes.TryGetValue(group.ShipTypeName, out var st)) continue;
            double speed = st.SpeedLoaded(group.Tech.Drive, group.Tech.Cargo, group.CargoLoad);

            if (speed <= 0)
            {
                // Stranded — stay put
                group.Destination = null;
                group.Distance    = 0;
                continue;
            }

            group.Distance -= speed;

            if (group.Distance <= 0)
            {
                // Arrived
                group.At          = group.Destination!;
                group.Destination = null;
                group.Distance    = 0;

                // Auto-unload at own/uninhabited planet
                if (player.AutoUnload)
                    TryAutoUnload(group, game.Planets.GetValueOrDefault(group.At), player);
            }
        }
    }

    /// <summary>Dispatch a group toward a destination planet.</summary>
    public void SendGroup(Group group, Planet destination, Game game, Player player)
    {
        if (!player.ShipTypes.TryGetValue(group.ShipTypeName, out var st)) return;

        var origin = game.Planets.GetValueOrDefault(group.At);
        if (origin is null) return;

        double dist   = origin.DistanceTo(destination);
        double speed  = st.SpeedLoaded(group.Tech.Drive, group.Tech.Cargo, group.CargoLoad);

        group.Origin      = group.At;
        group.Destination = destination.Name;
        group.Distance    = dist - speed;  // move happens immediately this turn
        group.LastRouteOrigin = group.Origin;
        group.LastRouteDestination = destination.Name;
        group.LastRouteTurn = game.Turn;
        group.LastRouteSpeed = speed;

        if (group.Distance <= 0)
        {
            // Arrives same turn
            group.At          = destination.Name;
            group.Destination = null;
            group.Distance    = 0;
            if (player.AutoUnload)
                TryAutoUnload(group, destination, player);
        }
        else
        {
            group.At = group.Origin!;
        }
    }

    /// <summary>Reverse a group in hyperspace (h order).</summary>
    public void ReverseGroup(Group group, Game game)
    {
        if (!group.InHyperspace) return;

        // Swap destination and origin, recalc distance from new origin
        var newDest   = group.Origin;
        group.Origin  = group.Destination;
        group.Destination = newDest;

        // Distance from the current position to new destination
        if (group.Destination is null) { group.Distance = 0; return; }
        if (!game.Planets.TryGetValue(group.Origin!, out var o)) return;
        if (!game.Planets.TryGetValue(group.Destination, out var d)) return;
        double total = o.DistanceTo(d);
        group.Distance = total - group.Distance; // remaining in reversed direction
        if (group.Distance < 0) group.Distance = 0;
    }

    private static void TryAutoUnload(Group group, Planet? planet, Player player)
    {
        if (planet is null) return;
        if (planet.OwnerId != player.Id && planet.OwnerId is not null) return;
        DoUnload(group, planet, player);
    }

    public static void DoUnload(Group group, Planet planet, Player player)
    {
        if (group.CargoType is null || group.CargoLoad <= 0) return;

        switch (group.CargoType)
        {
            case Constants.CargoCapital:
                planet.Stockpiles = planet.Stockpiles.WithCapital(
                    planet.Stockpiles.Capital + group.CargoLoad);
                break;
            case Constants.CargoMaterials:
                planet.Stockpiles = planet.Stockpiles.WithMaterials(
                    planet.Stockpiles.Materials + group.CargoLoad);
                break;
            case Constants.CargoColonists:
                if (!planet.IsOwned)
                {
                    // Colonize!
                    planet.OwnerId    = player.Id;
                    planet.Population = group.CargoLoad * Constants.PopToColonist;
                    planet.Industry   = 0;
                    planet.Producing  = ProductionType.Capital;
                }
                else
                {
                    planet.Population = Math.Min(
                        planet.Population + group.CargoLoad * Constants.PopToColonist,
                        planet.Size);
                }
                break;
        }

        group.CargoType = null;
        group.CargoLoad = 0;
    }

    public static void DoLoad(Group group, Planet planet, Player player,
        string cargoType, double? amount = null)
    {
        if (!player.ShipTypes.TryGetValue(group.ShipTypeName, out var st)) return;
        double capacity = st.BaseCargoCapacity * group.Tech.Cargo * group.Ships;
        group.CargoLoad = 0;
        group.CargoType = null;

        double available = cargoType switch
        {
            Constants.CargoCapital   => planet.Stockpiles.Capital,
            Constants.CargoMaterials => planet.Stockpiles.Materials,
            Constants.CargoColonists => planet.Stockpiles.Colonists,
            _                        => 0,
        };

        double load = amount.HasValue
            ? Math.Min(amount.Value, Math.Min(capacity, available))
            : Math.Min(capacity, available);

        if (load <= 0) return;

        group.CargoType = cargoType;
        group.CargoLoad = load;

        planet.Stockpiles = cargoType switch
        {
            Constants.CargoCapital   => planet.Stockpiles.WithCapital(planet.Stockpiles.Capital - load),
            Constants.CargoMaterials => planet.Stockpiles.WithMaterials(planet.Stockpiles.Materials - load),
            Constants.CargoColonists => planet.Stockpiles.WithColonists(planet.Stockpiles.Colonists - load),
            _                        => planet.Stockpiles,
        };
    }
}
