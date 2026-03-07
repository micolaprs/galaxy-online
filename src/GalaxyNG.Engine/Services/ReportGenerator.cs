using System.Text;
using GalaxyNG.Engine.Models;
using static GalaxyNG.Engine.Constants;

namespace GalaxyNG.Engine.Services;

public sealed class ReportGenerator
{
    public string GenerateTurnReport(Game game, Player player) =>
        new ReportBuilder(game, player).Build();

    private sealed class ReportBuilder(Game game, Player player)
    {
        private readonly StringBuilder _sb = new();

        public string Build()
        {
            Header();
            Status();
            MyShipTypes();
            AlienShipTypes();
            Battles();
            Bombings();
            Map();
            MyPlanets();
            MyGroups();
            AlienPlanets();
            UninhabitedPlanets();
            return _sb.ToString();
        }

        private void Header()
        {
            Line($"GalaxyNG Turn {game.Turn} — Game: {game.Name}");
            Line($"Race: {player.Name}  |  Drive: {player.Tech.Drive:F2}  Wpn: {player.Tech.Weapons:F2}  Shd: {player.Tech.Shields:F2}  Cargo: {player.Tech.Cargo:F2}");
            Line();
        }

        private void Status()
        {
            Section("STATUS OF PLAYERS");
            var header = $"{"Race",-20} {"Drive",6} {"Wpn",6} {"Shd",6} {"Cargo",6} {"Pops",8} {"Ind",8} {"Plnts",6}";
            Line(header);
            Line(new string('-', header.Length));

            foreach (var p in game.Players.Values.OrderByDescending(p =>
                game.PlanetsOwnedBy(p.Id).Sum(pl => pl.Production)))
            {
                var planets  = game.PlanetsOwnedBy(p.Id).ToList();
                var totalPop = planets.Sum(pl => pl.Population);
                var totalInd = planets.Sum(pl => pl.Industry);
                string diplo = player.Allies.Contains(p.Id) ? " [ALLY]" :
                               player.AtWar.Contains(p.Id)  ? " [WAR]"  : "";

                Line($"{p.Name + diplo,-20} {p.Tech.Drive,6:F2} {p.Tech.Weapons,6:F2} {p.Tech.Shields,6:F2} {p.Tech.Cargo,6:F2} {totalPop,8:F0} {totalInd,8:F0} {planets.Count,6}");
            }
            Line();
        }

        private void MyShipTypes()
        {
            if (player.ShipTypes.Count == 0) return;
            Section("YOUR SHIP TYPES");
            Line($"{"Name",-20} {"Drive",6} {"Atk",5} {"Wpn",6} {"Shd",6} {"Cargo",6} {"Mass",8} {"Speed",8}");
            foreach (var st in player.ShipTypes.Values)
                Line($"{st.Name,-20} {st.Drive,6:F2} {st.Attacks,5} {st.Weapons,6:F2} {st.Shields,6:F2} {st.Cargo,6:F2} {st.Mass,8:F2} {st.SpeedEmpty(player.Tech.Drive),8:F2}");
            Line();
        }

        private void AlienShipTypes()
        {
            var visible = VisibleAlienShipTypes();
            if (visible.Count == 0) return;
            Section("ALIEN SHIP TYPES");
            Line($"{"Race",-16} {"Name",-20} {"Drive",6} {"Atk",5} {"Wpn",6} {"Shd",6} {"Cargo",6} {"Mass",8}");
            foreach (var (race, st) in visible)
                Line($"{race,-16} {st.Name,-20} {st.Drive,6:F2} {st.Attacks,5} {st.Weapons,6:F2} {st.Shields,6:F2} {st.Cargo,6:F2} {st.Mass,8:F2}");
            Line();
        }

        private void Battles()
        {
            var relevant = game.Battles.Where(b => b.Participants.Contains(player.Name) ||
                                                    PlayerPlanets().Any(p => p.Name == b.PlanetName)).ToList();
            if (relevant.Count == 0) return;
            Section("BATTLES");
            foreach (var b in relevant)
                Line($"  {b.PlanetName}: {string.Join(" vs ", b.Participants)} → Winner: {b.Winner}");
            Line();
        }

        private void Bombings()
        {
            if (game.Bombings.Count == 0) return;
            Section("BOMBINGS");
            foreach (var b in game.Bombings)
                Line($"  {b.PlanetName}: {b.AttackerRace} bombed (prev owner: {b.PreviousOwner ?? "none"})  Pop: {b.OldPopulation:F0}→{b.OldPopulation * 0.25:F0}  Ind: {b.OldIndustry:F0}→{b.OldIndustry * 0.25:F0}");
            Line();
        }

        private void Map()
        {
            Section("MAP");
            int mapSize = 60;
            var grid    = new char[mapSize, mapSize];
            for (int r = 0; r < mapSize; r++)
            for (int c = 0; c < mapSize; c++)
                grid[r, c] = ' ';

            double scale = mapSize / game.GalaxySize;
            foreach (var planet in game.Planets.Values)
            {
                int col = Math.Clamp((int)(planet.X * scale), 0, mapSize - 1);
                int row = Math.Clamp((int)((game.GalaxySize - planet.Y) * scale), 0, mapSize - 1);
                grid[row, col] = planet.OwnerId == player.Id ? '*' :
                                  planet.OwnerId is null       ? 'o' : '+';
            }

            // Draw groups
            foreach (var g in player.Groups.Where(g => !g.InHyperspace))
            {
                if (!game.Planets.TryGetValue(g.At, out var p)) continue;
                int col = Math.Clamp((int)(p.X * scale), 0, mapSize - 1);
                int row = Math.Clamp((int)((game.GalaxySize - p.Y) * scale), 0, mapSize - 1);
                if (grid[row, col] == ' ') grid[row, col] = '.';
            }

            _sb.AppendLine("  " + new string('-', mapSize + 2));
            for (int r = 0; r < mapSize; r++)
            {
                _sb.Append("  |");
                for (int c = 0; c < mapSize; c++) _sb.Append(grid[r, c]);
                _sb.AppendLine("|");
            }
            _sb.AppendLine("  " + new string('-', mapSize + 2));
            Line("  Legend: * = your planets  + = enemy  o = uninhabited  . = your ships");
            Line();
        }

        private void MyPlanets()
        {
            Section("YOUR PLANETS");
            Line($"{"Name",-20} {"X",6} {"Y",6} {"Size",8} {"Pop",8} {"Ind",8} {"Res",6} {"Prod",-16} {"Cap",8} {"Mat",8} {"Col",6}");
            foreach (var p in PlayerPlanets().OrderBy(p => p.Name))
            {
                string prodLabel = p.Producing switch
                {
                    ProductionType.Ship => p.ShipTypeName ?? "?",
                    _                   => p.Producing.ToString().ToUpperInvariant()
                };
                Line($"{p.Name,-20} {p.X,6:F1} {p.Y,6:F1} {p.Size,8:F1} {p.Population,8:F1} {p.Industry,8:F1} {p.Resources,6:F2} {prodLabel,-16} {p.Stockpiles.Capital,8:F1} {p.Stockpiles.Materials,8:F1} {p.Stockpiles.Colonists,6:F1}");
            }
            Line();
        }

        private void MyGroups()
        {
            if (player.Groups.Count == 0) return;
            Section("YOUR GROUPS");
            Line($"{"#",4} {"Ships",6} {"Type",-20} {"Drive",6} {"Wpn",6} {"Shd",6} {"Cargo",-8} {"At",-16} {"Dest",-16} {"Dist",8}");
            foreach (var g in player.Groups.OrderBy(g => g.Number))
            {
                string cargo = g.CargoType is not null ? $"{g.CargoType}:{g.CargoLoad:F1}" : "-";
                Line($"{g.Number,4} {g.Ships,6} {g.ShipTypeName,-20} {g.Tech.Drive,6:F2} {g.Tech.Weapons,6:F2} {g.Tech.Shields,6:F2} {cargo,-8} {g.At,-16} {g.Destination ?? "-",-16} {g.Distance,8:F2}");
            }
            Line();
        }

        private void AlienPlanets()
        {
            // Planets where player has ships with intel
            var withIntel = game.Planets.Values
                .Where(p => p.OwnerId != player.Id && p.IsOwned &&
                            player.Groups.Any(g => g.At == p.Name && !g.InHyperspace))
                .ToList();
            if (withIntel.Count == 0) return;
            Section("ALIEN PLANETS (WITH INTEL)");
            foreach (var p in withIntel)
            {
                var owner = p.OwnerId is not null ? game.Players.GetValueOrDefault(p.OwnerId)?.Name ?? "?" : "?";
                Line($"  {p.Name} ({owner}) — Size: {p.Size:F1}  Pop: {p.Population:F1}  Ind: {p.Industry:F1}  Res: {p.Resources:F2}");
            }
            Line();
        }

        private void UninhabitedPlanets()
        {
            var visible = game.Planets.Values
                .Where(p => !p.IsOwned &&
                            player.Groups.Any(g => g.At == p.Name && !g.InHyperspace))
                .ToList();
            if (visible.Count == 0) return;
            Section("UNINHABITED PLANETS (SCOUTED)");
            foreach (var p in visible)
                Line($"  {p.Name}  X:{p.X:F1} Y:{p.Y:F1}  Size:{p.Size:F1}  Res:{p.Resources:F2}");
            Line();
        }

        // ---- helpers ----
        private IEnumerable<Planet> PlayerPlanets() =>
            game.Planets.Values.Where(p => p.OwnerId == player.Id);

        private List<(string race, ShipType st)> VisibleAlienShipTypes()
        {
            var visible = new List<(string, ShipType)>();
            var myPlanets = PlayerPlanets().Select(p => p.Name).ToHashSet();

            foreach (var other in game.Players.Values.Where(p => p.Id != player.Id))
            foreach (var g in other.Groups.Where(g => !g.InHyperspace && myPlanets.Contains(g.At)))
            {
                if (other.ShipTypes.TryGetValue(g.ShipTypeName, out var st))
                    visible.Add((other.Name, st));
            }

            return visible.DistinctBy(x => (x.Item1, x.Item2.Name)).ToList();
        }

        private void Section(string title)
        {
            Line($"{'=' + " " + title,-60}");
        }

        private void Line(string text = "") => _sb.AppendLine(text);
    }
}
