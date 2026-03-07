using GalaxyNG.Engine.Models;
using static GalaxyNG.Engine.Constants;

namespace GalaxyNG.Engine.Services;

public sealed class CombatResolver(Random? rng = null)
{
    private readonly Random _rng = rng ?? Random.Shared;

    /// <summary>
    /// Resolve all combats at a planet. Returns battle records and modifies groups in place.
    /// </summary>
    public BattleRecord? ResolveBattle(string planetName, Game game,
        List<(Player player, Group group)> presentGroups)
    {
        var warships = presentGroups
            .Where(x => IsWarship(x.player, x.group))
            .ToList();

        if (warships.Count < 2) return null;

        // Check if any two groups are at war
        bool anyConflict = false;
        for (int i = 0; i < warships.Count && !anyConflict; i++)
        for (int j = i + 1; j < warships.Count && !anyConflict; j++)
            if (AreAtWar(warships[i].player, warships[j].player))
                anyConflict = true;

        if (!anyConflict) return null;

        // Build combatant list (one entry per ship — flattened)
        var combatants = BuildCombatants(warships, game);
        var protocol   = new List<BattleShot>();

        // Battle rounds until one side wins or draw
        int maxRounds = 1000;
        while (maxRounds-- > 0)
        {
            // Reset fired flags
            foreach (var c in combatants) c.HasFired = false;

            // Each ship fires once per round in random order
            var order = combatants
                .Where(c => c.Alive)
                .OrderBy(_ => _rng.NextDouble())
                .ToList();

            foreach (var attacker in order)
            {
                if (!attacker.Alive || attacker.HasFired) continue;
                attacker.HasFired = true;

                var enemies = combatants
                    .Where(c => c.Alive && AreAtWar(attacker.Player, c.Player))
                    .ToList();
                if (enemies.Count == 0) continue;

                for (int gun = 0; gun < attacker.Attacks; gun++)
                {
                    var target = enemies[_rng.Next(enemies.Count)];
                    bool killed = DoShot(attacker, target);
                    protocol.Add(new BattleShot(attacker.Player.Name, target.Player.Name, killed));
                    if (killed)
                    {
                        target.Alive = false;
                        enemies.Remove(target);
                        if (enemies.Count == 0) break;
                    }
                }
            }

            var alive = combatants.Where(c => c.Alive).ToList();

            // Win check: all survivors are allied
            bool won = alive.Count == 0 ||
                       alive.All(c => alive.All(o => o == c || !AreAtWar(c.Player, o.Player)));
            // Draw check: every ship is invulnerable to every enemy
            bool draw = !won && alive.All(a =>
                alive.Where(e => AreAtWar(a.Player, e.Player))
                     .All(e => a.AttackStrength <= e.DefenseStrength * 0.001)); // effectively immune

            if (won || draw) break;
        }

        // Determine winner
        var survivors = combatants.Where(c => c.Alive).Select(c => c.Player.Name).Distinct().ToList();
        string winner = survivors.Count == 1 ? survivors[0] :
                        survivors.Count == 0 ? "None" : "Draw";

        // Update actual group ship counts
        ApplyCasualties(combatants, warships);

        return new BattleRecord(
            planetName,
            winner,
            [.. warships.Select(w => w.player.Name).Distinct()],
            protocol);
    }

    // --- bombing ---
    public BombingRecord? DoBombing(Planet planet, List<(Player player, Group group)> warshipGroups, Game game)
    {
        var attackers = warshipGroups
            .Where(w => w.player.Id != planet.OwnerId && IsWarship(w.player, w.group))
            .ToList();

        if (attackers.Count == 0 || !planet.IsOwned) return null;

        var prevOwner = planet.OwnerId;
        var prevPop   = planet.Population;
        var prevInd   = planet.Industry;

        planet.Population = Math.Round(planet.Population * (1 - BombingReduction), 2);
        planet.Industry   = Math.Round(planet.Industry   * (1 - BombingReduction), 2);
        planet.Producing  = ProductionType.Capital;
        planet.ShipTypeName = null;

        // Change ownership if only one attacker race left at planet
        var allAtPlanet = game.Players.Values
            .SelectMany(p => p.Groups.Select(g => (player: p, group: g)))
            .Where(x => x.group.At == planet.Name && !x.group.InHyperspace)
            .ToList();

        var racesAtPlanet = allAtPlanet.Select(x => x.player.Id).Distinct().ToList();
        if (racesAtPlanet.Count == 1)
            planet.OwnerId = racesAtPlanet[0];
        else if (racesAtPlanet.Count == 0 || (planet.OwnerId is not null && !racesAtPlanet.Contains(planet.OwnerId!)))
            planet.OwnerId = null; // contested — unowned

        return new BombingRecord(
            planet.Name,
            attackers[0].player.Name,
            prevOwner is not null ? game.Players.GetValueOrDefault(prevOwner)?.Name : null,
            prevPop,
            prevInd,
            "CAP");
    }

    // ---- internals ----

    private bool DoShot(Combatant attacker, Combatant defender)
    {
        if (attacker.AttackStrength == 0) return false;
        double ratio = attacker.AttackStrength / Math.Max(defender.DefenseStrength, 0.0001);
        // kill if ratio > 4^random   ↔   log4(ratio) > random
        double threshold = Math.Log(ratio) / Math.Log(BattleBase);
        double randomVal = _rng.NextDouble();
        return threshold > randomVal; // P[kill] = (log4(A/D) + 1) / 2
    }

    private static bool IsWarship(Player player, Group group) =>
        player.ShipTypes.TryGetValue(group.ShipTypeName, out var st) && st.IsWarship;

    private static bool AreAtWar(Player a, Player b) =>
        a.Id != b.Id && (a.AtWar.Contains(b.Id) || !a.Allies.Contains(b.Id));

    private List<Combatant> BuildCombatants(
        List<(Player player, Group group)> warships, Game game)
    {
        var list = new List<Combatant>();
        foreach (var (player, group) in warships)
        {
            if (!player.ShipTypes.TryGetValue(group.ShipTypeName, out var st)) continue;
            for (int i = 0; i < group.Ships; i++)
            {
                list.Add(new Combatant
                {
                    Player          = player,
                    Group           = group,
                    AttackStrength  = st.AttackStrength(group.Tech.Weapons),
                    DefenseStrength = st.DefenseStrength(group.Tech.Shields, group.Tech.Cargo, group.CargoLoad),
                    Attacks         = st.Attacks,
                });
            }
        }
        return list;
    }

    private static void ApplyCasualties(List<Combatant> combatants, List<(Player player, Group group)> warships)
    {
        // Count survivors per group
        var survivorCounts = combatants
            .Where(c => c.Alive)
            .GroupBy(c => (c.Player.Id, c.Group.Number))
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (player, group) in warships)
        {
            var key = (player.Id, group.Number);
            group.Ships = survivorCounts.GetValueOrDefault(key, 0);
        }

        // Remove groups with 0 ships
        foreach (var (player, _) in warships)
            player.Groups.RemoveAll(g => g.Ships <= 0);
    }

    private sealed class Combatant
    {
        public required Player Player           { get; init; }
        public required Group  Group            { get; init; }
        public required double AttackStrength   { get; init; }
        public required double DefenseStrength  { get; init; }
        public required int    Attacks          { get; init; }
        public bool            Alive            { get; set; } = true;
        public bool            HasFired         { get; set; }
    }
}
