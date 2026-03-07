using GalaxyNG.Engine.Models;
using static GalaxyNG.Engine.Constants;

namespace GalaxyNG.Engine.Services;

public sealed class OrderValidator(Game game, Player player)
{
    public record ValidationResult(bool Ok, string? Error = null)
    {
        public static ValidationResult Success => new(true);
        public static ValidationResult Fail(string e) => new(false, e);
    }

    public List<ValidationResult> ValidateAll(IEnumerable<ParsedOrder> orders)
    {
        var results = new List<ValidationResult>();
        foreach (var order in orders)
            results.Add(Validate(order));
        return results;
    }

    public ValidationResult Validate(ParsedOrder o) => o.Kind switch
    {
        OrderKind.DesignShip     => ValidateDesign(o.Args),
        OrderKind.SetProduction  => ValidateProduction(o.Args),
        OrderKind.SendGroup      => ValidateSend(o.Args),
        OrderKind.LoadCargo      => ValidateLoad(o.Args),
        OrderKind.UnloadCargo    => ValidateUnload(o.Args),
        OrderKind.BreakGroup     => ValidateBreak(o.Args),
        _                        => ValidationResult.Success,
    };

    private ValidationResult ValidateDesign(string[] args)
    {
        if (!double.TryParse(args[1], out double drive) || drive < 0)
            return ValidationResult.Fail("Drive mass must be >= 0.");
        if (!int.TryParse(args[2], out int attacks) || attacks < 0)
            return ValidationResult.Fail("Attacks must be >= 0.");
        if (!double.TryParse(args[3], out double wpn) || (wpn > 0 && wpn < 1))
            return ValidationResult.Fail("Weapons mass must be 0 or >= 1.");
        if (!double.TryParse(args[4], out double shd) || (shd > 0 && shd < 1))
            return ValidationResult.Fail("Shields mass must be 0 or >= 1.");
        if (!double.TryParse(args[5], out double cargo) || (cargo > 0 && cargo < 1))
            return ValidationResult.Fail("Cargo mass must be 0 or >= 1.");
        if (drive + wpn + shd + cargo == 0)
            return ValidationResult.Fail("Ship must have at least some mass.");
        return ValidationResult.Success;
    }

    private ValidationResult ValidateProduction(string[] args)
    {
        string planet = args[0], prod = args[1].ToUpperInvariant();
        if (!game.Planets.TryGetValue(planet, out var p) || p.OwnerId != player.Id)
            return ValidationResult.Fail($"Planet '{planet}' not owned by you.");
        if (prod is ProdDrive or ProdWeapons or ProdShields or ProdCargo or ProdCap or ProdMat)
            return ValidationResult.Success;
        if (player.ShipTypes.ContainsKey(prod))
            return ValidationResult.Success;
        return ValidationResult.Fail($"Unknown production type '{prod}'.");
    }

    private ValidationResult ValidateSend(string[] args)
    {
        if (!int.TryParse(args[0], out int num))
            return ValidationResult.Fail("Group number must be an integer.");
        if (!player.Groups.Any(g => g.Number == num))
            return ValidationResult.Fail($"Group {num} not found.");
        if (!game.Planets.ContainsKey(args[1]))
            return ValidationResult.Fail($"Planet '{args[1]}' not found.");
        return ValidationResult.Success;
    }

    private ValidationResult ValidateLoad(string[] args)
    {
        if (!int.TryParse(args[0], out int num))
            return ValidationResult.Fail("Group number must be integer.");
        var grp = player.Groups.FirstOrDefault(g => g.Number == num);
        if (grp is null) return ValidationResult.Fail($"Group {num} not found.");
        string cargo = args[1].ToUpperInvariant();
        if (cargo is not (CargoCapital or CargoColonists or CargoMaterials))
            return ValidationResult.Fail($"Unknown cargo type '{cargo}'.");
        return ValidationResult.Success;
    }

    private ValidationResult ValidateUnload(string[] args)
    {
        if (!int.TryParse(args[0], out int num))
            return ValidationResult.Fail("Group number must be integer.");
        if (!player.Groups.Any(g => g.Number == num))
            return ValidationResult.Fail($"Group {num} not found.");
        return ValidationResult.Success;
    }

    private ValidationResult ValidateBreak(string[] args)
    {
        if (!int.TryParse(args[0], out int num))
            return ValidationResult.Fail("Group number must be integer.");
        var grp = player.Groups.FirstOrDefault(g => g.Number == num);
        if (grp is null) return ValidationResult.Fail($"Group {num} not found.");
        if (!int.TryParse(args[1], out int ships) || ships <= 0 || ships >= grp.Ships)
            return ValidationResult.Fail($"Break count must be 1..{grp.Ships - 1}.");
        return ValidationResult.Success;
    }
}
