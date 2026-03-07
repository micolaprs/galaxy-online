using GalaxyNG.Engine.Models;

namespace GalaxyNG.Engine.Services;

public sealed class OrderParser
{
    /// <summary>
    /// Parses orders text into a list of <see cref="ParsedOrder"/>.
    /// Follows original GalaxyNG format: first char of line determines command.
    /// </summary>
    public (List<ParsedOrder> orders, List<string> errors) Parse(string text)
    {
        var orders     = new List<ParsedOrder>();
        var errors     = new List<string>();
        bool inMessage = false;
        var  msgLines  = new List<string>();
        string[] msgTargets = [];

        int lineNum = 0;
        foreach (var rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNum++;
            var line = inMessage ? rawLine : StripComment(rawLine);
            line = line.Trim();
            if (line.Length == 0) continue;

            // Handle @ message block end (another @ closes it)
            if (inMessage)
            {
                if (line[0] == '@')
                {
                    orders.Add(new ParsedOrder(OrderKind.SendMessage,
                        [.. msgTargets, string.Join("\n", msgLines)], lineNum));
                    inMessage = false;
                    msgLines.Clear();
                }
                else
                    msgLines.Add(rawLine);
                continue;
            }

            var parts = SplitLine(line);
            if (parts.Length == 0) continue;

            char cmd = char.ToLowerInvariant(parts[0][0]);

            try
            {
                if (cmd == '@')
                {
                    inMessage  = true;
                    msgTargets = parts[1..];
                    continue;
                }

                var order = ParseLine(cmd, parts, lineNum);
                if (order is not null)
                    orders.Add(order);
            }
            catch (OrderParseException ex)
            {
                errors.Add($"Line {lineNum}: {ex.Message}");
            }
        }

        if (inMessage && msgLines.Count > 0)
            orders.Add(new ParsedOrder(OrderKind.SendMessage,
                [.. msgTargets, string.Join("\n", msgLines)]));

        return (orders, errors);
    }

    private static ParsedOrder? ParseLine(char cmd, string[] parts, int lineNum) =>
        cmd switch
        {
            'c' => Require(parts, 2, OrderKind.ChangeName, lineNum),
            'y' => Require(parts, 2, OrderKind.ChangePassword, lineNum),
            'z' => Require(parts, 2, OrderKind.ChangeEmail, lineNum),
            '=' => new ParsedOrder(OrderKind.SetRealName, [string.Join(" ", parts[1..])], lineNum),
            'q' => Require(parts, 2, OrderKind.Quit, lineNum),

            'n' => Require(parts, 3, OrderKind.RenamePlanet, lineNum),
            'p' => Require(parts, 3, OrderKind.SetProduction, lineNum),
            'r' => Require(parts, 3, OrderKind.SetRoute, lineNum),
            'v' => Require(parts, 2, OrderKind.Victory, lineNum),
            'm' => Require(parts, 4, OrderKind.SetMapView, lineNum),

            'd' when IsFleetKeyword(parts, 1)
                  => Require(parts, 3, OrderKind.CreateFleet, lineNum),
            'd'   => Require(parts, 7, OrderKind.DesignShip, lineNum),

            't' => Require(parts, 3, OrderKind.RenameType, lineNum),

            'e' when IsFleetKeyword(parts, 1)
                  => Require(parts, 2, OrderKind.EliminateFleet, lineNum),
            'e'   => Require(parts, 2, OrderKind.EliminateType, lineNum),

            's' => ParseGroupOrFleet(parts, OrderKind.SendGroup, OrderKind.SendFleet, lineNum),
            'i' => ParseGroupOrFleet(parts, OrderKind.InterceptGroup, OrderKind.InterceptFleet, lineNum),
            'l' => Require(parts, 3, OrderKind.LoadCargo, lineNum),
            'u' => Require(parts, 2, OrderKind.UnloadCargo, lineNum),
            'g' => Require(parts, 2, OrderKind.UpgradeGroup, lineNum),
            'b' => Require(parts, 3, OrderKind.BreakGroup, lineNum),
            'x' => Require(parts, 2, OrderKind.ScrapGroup, lineNum),
            'h' => ParseGroupOrFleet(parts, OrderKind.ReverseGroup, OrderKind.ReverseFleet, lineNum),

            'j' => Require(parts, 3, OrderKind.JoinFleet, lineNum),

            'a' => Require(parts, 2, OrderKind.DeclareAlliance, lineNum),
            'w' => Require(parts, 2, OrderKind.DeclareWar, lineNum),
            'f' => Require(parts, 2, OrderKind.RequestEmail, lineNum),

            'o' => Require(parts, 2, OrderKind.SetOption, lineNum),

            _   => null,  // unknown command — silently ignored like original
        };

    private static bool IsFleetKeyword(string[] parts, int idx) =>
        parts.Length > idx &&
        parts[idx].Equals("fleet", StringComparison.OrdinalIgnoreCase);

    private static ParsedOrder Require(string[] parts, int minArgs, OrderKind kind, int lineNum)
    {
        if (parts.Length < minArgs)
            throw new OrderParseException($"'{parts[0]}' requires at least {minArgs - 1} argument(s).");
        return new ParsedOrder(kind, parts[1..], lineNum);
    }

    private static ParsedOrder ParseGroupOrFleet(string[] parts, OrderKind groupKind, OrderKind fleetKind, int lineNum)
    {
        if (parts.Length < 2)
            throw new OrderParseException($"'{parts[0]}' requires at least 1 argument.");
        bool isFleet = !int.TryParse(parts[1], out _);
        return new ParsedOrder(isFleet ? fleetKind : groupKind, parts[1..], lineNum);
    }

    private static string StripComment(string line)
    {
        int semi = line.IndexOf(';');
        return semi >= 0 ? line[..semi] : line;
    }

    private static string[] SplitLine(string line) =>
        line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
}

file sealed class OrderParseException(string message) : Exception(message);
