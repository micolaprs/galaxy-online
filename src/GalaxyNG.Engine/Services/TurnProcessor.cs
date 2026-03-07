using GalaxyNG.Engine.Models;
using static GalaxyNG.Engine.Constants;

namespace GalaxyNG.Engine.Services;

/// <summary>
/// Executes one full GalaxyNG turn in the canonical 9-phase order:
///  1. Apply account/rename orders
///  2. Alliance/war declarations
///  3. Pre-move combat
///  4. Bombing (pre-move)
///  5. Load/unload cargo
///  6. Upgrade groups
///  7. Dispatch groups into hyperspace
///  8. Routes assigned, cargo ships launched
///  9. Move groups through hyperspace
/// 10. Post-move combat
/// 11. Post-move bombing
/// 12. Production
/// 13. Population growth
/// 14. Auto-unload at destinations
/// 15. Merge identical groups
/// 16. Renumber groups (if SortGroups)
/// 17. Renames applied
/// </summary>
public sealed class TurnProcessor(
    CombatResolver   combat,
    ProductionEngine production,
    MovementEngine   movement)
{
    public void RunTurn(Game game)
    {
        game.Battles  = [];
        game.Bombings = [];
        game.Turn++;

        // Phase 1: account orders, messages, renames queued
        foreach (var player in game.Players.Values)
            ApplyAccountOrders(player, game);

        // Phase 2: alliances / war
        foreach (var player in game.Players.Values)
            ApplyDiplomacyOrders(player, game);

        // Phase 3 & 4: pre-move combat and bombing (stationary groups)
        RunCombatAndBombing(game, "pre-move");

        // Phase 5: load / unload
        foreach (var player in game.Players.Values)
            ApplyCargoOrders(player, game);

        // Phase 6: upgrade
        foreach (var player in game.Players.Values)
            ApplyUpgradeOrders(player, game);

        // Phase 7 & 8: send / intercept / routes
        foreach (var player in game.Players.Values)
            ApplyMovementOrders(player, game);

        // Phase 9: move through hyperspace
        movement.MoveGroups(game);

        // Phase 10 & 11: post-move combat and bombing
        RunCombatAndBombing(game, "post-move");

        // Phase 12: production
        production.RunProduction(game);

        // Phase 13: pop growth
        production.GrowPopulation(game);

        // Phase 14: auto-unload at destinations (covered in MoveGroups)

        // Phase 15: merge identical groups
        foreach (var player in game.Players.Values)
            MergeGroups(player);

        // Phase 16: renumber
        foreach (var player in game.Players.Values.Where(p => p.SortGroups))
            RenumberGroups(player);

        // Phase 17: apply pending renames
        foreach (var player in game.Players.Values)
            ApplyRenameOrders(player, game);

        // Reset submission flags
        foreach (var player in game.Players.Values)
        {
            player.Submitted = false;
            player.Orders    = [];
            player.PendingOrders = "";
        }
    }

    // ==================== ORDER APPLICATORS ====================

    private static void ApplyAccountOrders(Player player, Game game)
    {
        foreach (var order in player.Orders)
        {
            switch (order.Kind)
            {
                case OrderKind.ChangePassword when order.Args.Length >= 1:
                    player.Password = order.Args[0];
                    break;
                case OrderKind.ChangeEmail when order.Args.Length >= 1:
                    player.Email = order.Args[0];
                    break;
                case OrderKind.SetRealName when order.Args.Length >= 1:
                    player.RealName = string.Join(" ", order.Args);
                    break;
                case OrderKind.SetOption when order.Args.Length >= 1:
                    ApplyOption(player, order.Args);
                    break;
                case OrderKind.DesignShip when order.Args.Length >= 6:
                    DesignShip(player, order.Args);
                    break;
                case OrderKind.EliminateType when order.Args.Length >= 1:
                    player.ShipTypes.Remove(order.Args[0]);
                    break;
                case OrderKind.CreateFleet when order.Args.Length >= 2:
                    player.FleetNames.Add(order.Args[1]);
                    break;
                case OrderKind.EliminateFleet when order.Args.Length >= 2:
                    player.FleetNames.Remove(order.Args[1]);
                    foreach (var g in player.Groups.Where(g => g.FleetName == order.Args[1]))
                        g.FleetName = null;
                    break;
                case OrderKind.SetProduction when order.Args.Length >= 2:
                    SetProduction(player, game, order.Args[0], order.Args[1]);
                    break;
                case OrderKind.SetRoute when order.Args.Length >= 2:
                    SetRoute(game, player, order.Args);
                    break;
                case OrderKind.SendMessage when order.Args.Length >= 1:
                    SaveDiplomacyMessage(game, player, order.Args);
                    break;
                case OrderKind.Quit when order.Args.Length >= 1:
                    ApplyQuit(player, game, order.Args[0]);
                    break;
            }
        }
    }

    private static void DesignShip(Player player, string[] args)
    {
        if (!double.TryParse(args[1], out double drive))  return;
        if (!int.TryParse(args[2],    out int attacks))   return;
        if (!double.TryParse(args[3], out double weapons)) return;
        if (!double.TryParse(args[4], out double shields)) return;
        if (!double.TryParse(args[5], out double cargo))   return;
        string name = args[0];
        player.ShipTypes[name] = new ShipType
        {
            Name    = name,
            Drive   = drive,
            Attacks = attacks,
            Weapons = weapons,
            Shields = shields,
            Cargo   = cargo,
        };
    }

    private static void SetProduction(Player player, Game game, string planetName, string prodType)
    {
        if (!game.Planets.TryGetValue(planetName, out var planet)) return;
        if (planet.OwnerId != player.Id) return;

        planet.Producing = prodType.ToUpperInvariant() switch
        {
            ProdDrive   => ProductionType.Drive,
            ProdWeapons => ProductionType.Weapons,
            ProdShields => ProductionType.Shields,
            ProdCargo   => ProductionType.Cargo,
            ProdCap     => ProductionType.Capital,
            ProdMat     => ProductionType.Materials,
            _           => ProductionType.Ship,
        };

        if (planet.Producing == ProductionType.Ship)
        {
            if (!player.ShipTypes.ContainsKey(prodType)) { planet.Producing = ProductionType.Capital; return; }
            if (planet.ShipTypeName != prodType) planet.ExcessProd = 0;
            planet.ShipTypeName = prodType;
        }
        else
        {
            planet.ShipTypeName = null;
            planet.ExcessProd   = 0;
        }
    }

    private static void SetRoute(Game game, Player player, string[] args)
    {
        string origin  = args[0];
        string cargo   = args[1].ToUpperInvariant();
        string? dest   = args.Length >= 3 ? args[2] : null;

        if (!game.Planets.TryGetValue(origin, out var planet)) return;
        if (planet.OwnerId != player.Id) return;
        if (planet.Routes.ContainsKey(cargo))
            planet.Routes[cargo] = dest;
    }

    private static void ApplyOption(Player player, string[] args)
    {
        bool enable = !args[0].Equals("NO", StringComparison.OrdinalIgnoreCase);
        string option = enable ? args[0] : (args.Length > 1 ? args[1] : "");
        switch (option.ToUpperInvariant())
        {
            case "AUTOUNLOAD":  player.AutoUnload  = enable; break;
            case "SORTGROUPS":  player.SortGroups  = enable; break;
        }
    }

    private static void SaveDiplomacyMessage(Game game, Player sender, string[] args)
    {
        var text = args[^1].Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var recipients = ResolveRecipients(game, sender, args[..^1]);
        var message = new DiplomacyMessage
        {
            Turn = game.Turn,
            SentAt = DateTime.UtcNow,
            SenderId = sender.Id,
            SenderName = sender.Name,
            RecipientIds = recipients,
            Text = text,
        };

        game.DiplomacyMessages.Add(message);
        const int maxMessages = 500;
        if (game.DiplomacyMessages.Count > maxMessages)
            game.DiplomacyMessages.RemoveRange(0, game.DiplomacyMessages.Count - maxMessages);
    }

    private static List<string> ResolveRecipients(Game game, Player sender, IReadOnlyList<string> targets)
    {
        if (targets.Count == 0)
            return [];

        if (targets.Any(IsGlobalTarget))
            return [];

        var recipientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            foreach (var token in target.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var player = game.GetPlayer(token);
                if (player is null || player.Id == sender.Id)
                    continue;
                recipientIds.Add(player.Id);
            }
        }

        return [.. recipientIds];
    }

    private static bool IsGlobalTarget(string token)
    {
        var normalized = token.Trim().ToUpperInvariant();
        return normalized is "*" or "ALL" or "GALAXY" or "EVERYONE";
    }

    private static void ApplyQuit(Player player, Game game, string targetToken)
    {
        if (player.IsEliminated)
            return;

        var target = game.GetPlayer(targetToken);
        if (target is not null && target.Id != player.Id && !target.IsEliminated)
        {
            foreach (var planet in game.PlanetsOwnedBy(player.Id))
                planet.OwnerId = target.Id;

            foreach (var group in player.Groups)
                target.Groups.Add(group);
            player.Groups.Clear();

            player.Allies.Add(target.Id);
            target.Allies.Add(player.Id);
        }
        else
        {
            foreach (var planet in game.PlanetsOwnedBy(player.Id))
                planet.OwnerId = null;
            player.Groups.Clear();
        }

        player.IsEliminated = true;
    }

    private static void ApplyDiplomacyOrders(Player player, Game game)
    {
        foreach (var order in player.Orders)
        {
            switch (order.Kind)
            {
                case OrderKind.DeclareAlliance when order.Args.Length >= 1:
                {
                    var target = game.GetPlayer(order.Args[0]);
                    if (target is null) break;
                    player.Allies.Add(target.Id);
                    player.AtWar.Remove(target.Id);
                    break;
                }
                case OrderKind.DeclareWar when order.Args.Length >= 1:
                {
                    var target = game.GetPlayer(order.Args[0]);
                    if (target is null) break;
                    player.AtWar.Add(target.Id);
                    player.Allies.Remove(target.Id);
                    break;
                }
            }
        }
    }

    private void ApplyCargoOrders(Player player, Game game)
    {
        foreach (var order in player.Orders)
        {
            switch (order.Kind)
            {
                case OrderKind.LoadCargo when order.Args.Length >= 2:
                {
                    if (!int.TryParse(order.Args[0], out int num)) break;
                    var grp = player.Groups.FirstOrDefault(g => g.Number == num);
                    if (grp is null || grp.InHyperspace) break;
                    var planet = game.Planets.GetValueOrDefault(grp.At);
                    if (planet is null || planet.OwnerId != player.Id) break;
                    string cargo  = order.Args[1].ToUpperInvariant();
                    double? amount = order.Args.Length >= 3 && double.TryParse(order.Args[2], out double a) ? a : null;
                    MovementEngine.DoLoad(grp, planet, player, cargo, amount);
                    break;
                }
                case OrderKind.UnloadCargo when order.Args.Length >= 1:
                {
                    if (!int.TryParse(order.Args[0], out int num)) break;
                    var grp = player.Groups.FirstOrDefault(g => g.Number == num);
                    if (grp is null || grp.InHyperspace) break;
                    var planet = game.Planets.GetValueOrDefault(grp.At);
                    if (planet is null) break;
                    MovementEngine.DoUnload(grp, planet, player);
                    break;
                }
            }
        }
    }

    private static void ApplyUpgradeOrders(Player player, Game game)
    {
        foreach (var order in player.Orders.Where(o => o.Kind == OrderKind.UpgradeGroup))
        {
            if (!int.TryParse(order.Args[0], out int num)) continue;
            var grp = player.Groups.FirstOrDefault(g => g.Number == num);
            if (grp is null || grp.InHyperspace) continue;
            var planet = game.Planets.GetValueOrDefault(grp.At);
            if (planet is null || planet.OwnerId != player.Id) continue;
            if (!player.ShipTypes.TryGetValue(grp.ShipTypeName, out var st)) continue;

            double upgradeCost = CalculateUpgradeCost(grp, player, st);
            double available   = planet.Production;
            if (available >= upgradeCost)
            {
                planet.ExcessProd -= upgradeCost;
                grp.Tech = player.Tech;
            }
            else
            {
                // Partial upgrade
                double ratio = available / upgradeCost;
                grp.Tech = grp.Tech with
                {
                    Drive   = grp.Tech.Drive   + ratio * (player.Tech.Drive   - grp.Tech.Drive),
                    Weapons = grp.Tech.Weapons + ratio * (player.Tech.Weapons - grp.Tech.Weapons),
                    Shields = grp.Tech.Shields + ratio * (player.Tech.Shields - grp.Tech.Shields),
                    Cargo   = grp.Tech.Cargo   + ratio * (player.Tech.Cargo   - grp.Tech.Cargo),
                };
            }
        }
    }

    private static double CalculateUpgradeCost(Group grp, Player owner, ShipType st)
    {
        double cost =
            (1 - grp.Tech.Drive   / owner.Tech.Drive)   * st.Drive   +
            (1 - grp.Tech.Weapons / owner.Tech.Weapons) * st.Weapons +
            (1 - grp.Tech.Shields / owner.Tech.Shields) * st.Shields +
            (1 - grp.Tech.Cargo   / owner.Tech.Cargo)   * st.Cargo;
        return cost * 10 * grp.Ships;
    }

    private void ApplyMovementOrders(Player player, Game game)
    {
        foreach (var order in player.Orders)
        {
            switch (order.Kind)
            {
                case OrderKind.SendGroup when order.Args.Length >= 2:
                {
                    if (!int.TryParse(order.Args[0], out int num)) break;
                    var grp  = player.Groups.FirstOrDefault(g => g.Number == num);
                    var dest = game.GetPlanet(order.Args[1]);
                    if (grp is null || dest is null) break;
                    movement.SendGroup(grp, dest, game, player);
                    break;
                }
                case OrderKind.ReverseGroup when order.Args.Length >= 1:
                {
                    if (!int.TryParse(order.Args[0], out int num)) break;
                    var grp = player.Groups.FirstOrDefault(g => g.Number == num);
                    if (grp is not null) movement.ReverseGroup(grp, game);
                    break;
                }
                case OrderKind.SendFleet when order.Args.Length >= 2:
                {
                    string fleetName = order.Args[0];
                    var dest = game.GetPlanet(order.Args[1]);
                    if (dest is null) break;
                    foreach (var grp in player.Groups.Where(g => g.FleetName == fleetName))
                        movement.SendGroup(grp, dest, game, player);
                    break;
                }
                case OrderKind.BreakGroup when order.Args.Length >= 2:
                {
                    if (!int.TryParse(order.Args[0], out int num)) break;
                    if (!int.TryParse(order.Args[1], out int ships)) break;
                    var grp = player.Groups.FirstOrDefault(g => g.Number == num);
                    if (grp is null || ships <= 0 || ships >= grp.Ships) break;
                    var newGrp = grp.Clone();
                    newGrp.Number = player.NextGroupNumber();
                    newGrp.Ships  = ships;
                    grp.Ships    -= ships;
                    player.Groups.Add(newGrp);
                    break;
                }
                case OrderKind.ScrapGroup when order.Args.Length >= 1:
                {
                    if (!int.TryParse(order.Args[0], out int num)) break;
                    var grp    = player.Groups.FirstOrDefault(g => g.Number == num);
                    var planet = grp is not null ? game.Planets.GetValueOrDefault(grp.At) : null;
                    if (grp is null || planet is null || grp.InHyperspace) break;
                    if (!player.ShipTypes.TryGetValue(grp.ShipTypeName, out var st)) break;
                    planet.Stockpiles = planet.Stockpiles.WithMaterials(
                        planet.Stockpiles.Materials + st.Mass * grp.Ships * 0.5);
                    player.Groups.Remove(grp);
                    break;
                }
                case OrderKind.JoinFleet when order.Args.Length >= 2:
                {
                    if (!int.TryParse(order.Args[0], out int num)) break;
                    var grp = player.Groups.FirstOrDefault(g => g.Number == num);
                    if (grp is not null) grp.FleetName = order.Args[1];
                    break;
                }
                case OrderKind.RenamePlanet when order.Args.Length >= 2:
                {
                    var planet = game.GetPlanet(order.Args[0]);
                    if (planet is null || planet.OwnerId != player.Id) break;
                    planet.Name = order.Args[1];
                    break;
                }
            }
        }
    }

    private static void ApplyRenameOrders(Player player, Game game) { /* handled in movement */ }

    // ==================== COMBAT ====================

    private void RunCombatAndBombing(Game game, string phase)
    {
        foreach (var planet in game.Planets.Values)
        {
            var present = game.Players.Values
                .SelectMany(p => p.Groups.Select(g => (player: p, group: g)))
                .Where(x => x.group.At == planet.Name && !x.group.InHyperspace)
                .ToList();

            if (present.Count < 2) continue;

            // Combat
            var battle = combat.ResolveBattle(planet.Name, game, present);
            if (battle is not null) game.Battles.Add(battle);

            // Refresh present after combat (some groups may be destroyed)
            var afterCombat = game.Players.Values
                .SelectMany(p => p.Groups.Select(g => (player: p, group: g)))
                .Where(x => x.group.At == planet.Name && !x.group.InHyperspace)
                .ToList();

            // Bombing
            var bombing = combat.DoBombing(planet, afterCombat, game);
            if (bombing is not null) game.Bombings.Add(bombing);
        }
    }

    // ==================== GROUP MANAGEMENT ====================

    private static void MergeGroups(Player player)
    {
        var toRemove = new List<Group>();
        var groups   = player.Groups;

        for (int i = 0; i < groups.Count; i++)
        {
            if (toRemove.Contains(groups[i])) continue;
            for (int j = i + 1; j < groups.Count; j++)
            {
                if (toRemove.Contains(groups[j])) continue;
                if (CanMerge(groups[i], groups[j]))
                {
                    groups[i].Ships += groups[j].Ships;
                    toRemove.Add(groups[j]);
                }
            }
        }
        foreach (var g in toRemove) groups.Remove(g);
    }

    private static bool CanMerge(Group a, Group b) =>
        a.ShipTypeName == b.ShipTypeName &&
        a.At           == b.At           &&
        a.Destination  == b.Destination  &&
        a.Tech         == b.Tech         &&
        a.CargoType    == b.CargoType    &&
        Math.Abs(a.CargoLoad - b.CargoLoad) < 0.01 &&
        a.FleetName    == b.FleetName;

    private static void RenumberGroups(Player player)
    {
        var sorted = player.Groups
            .OrderBy(g => g.At)
            .ThenBy(g => g.ShipTypeName)
            .ToList();
        for (int i = 0; i < sorted.Count; i++)
            sorted[i].Number = i + 1;
    }
}
