namespace GalaxyNG.Engine;

public static class Constants
{
    // Physics
    public const double DriveMultiplier  = 20.0;
    public const double PopGrowthRate    = 0.08;
    public const double ColonistRatio    = 8.0;   // 8 pop = 1 colonist unit
    public const double PopToColonist    = 8.0;   // colonist = 8 population
    public const double BombingReduction = 0.75;  // 75% pop & industry reduced
    public const double ProdIndustryW    = 0.75;
    public const double ProdPopW         = 0.25;

    // Research costs (production per +1.0 tech)
    public const double ResearchDriveCost   = 5_000.0;
    public const double ResearchWeaponsCost = 5_000.0;
    public const double ResearchShieldsCost = 5_000.0;
    public const double ResearchCargoCost   = 2_500.0;

    // Ship costs
    public const double ShipProdPerMass     = 10.0;
    public const double ShipMatPerMass      = 1.0;
    public const double CapitalProdCost     = 5.0;
    public const double CapitalMatCost      = 1.0;

    // Combat  — P[kill] = (log4(attack/defense) + 1) / 2
    // kill if attack/defense > 4^random  ↔  random > log4(attack/defense)^-1
    public const double BattleBase = 4.0;
    public const double DefenseCubeFactor = 30.0; // from original: 30^(1/3) ≈ 3.107

    // Galaxy generation defaults
    public const int    DefaultGalaxySize    = 200;
    public const double DefaultMinDist       = 25.0;
    public const double DefaultEmptyRadius   = 30.0;
    public const int    DefaultStuffPlanets  = 5;
    public const int    DefaultHomePlanets   = 2;   // secondary homeworlds per player

    // Home planet
    public const double HomeResources = 10.0;
    public const double HomeSize      = 500.0;

    // Limits
    public const int MaxNameLength = 20;
    public const int MaxTurns      = 9999;

    // Cargo types
    public const string CargoCapital    = "CAP";
    public const string CargoColonists  = "COL";
    public const string CargoMaterials  = "MAT";
    public const string CargoEmpty      = "EMP";

    // Production types
    public const string ProdDrive   = "DRIVE";
    public const string ProdWeapons = "WEAPONS";
    public const string ProdShields = "SHIELDS";
    public const string ProdCargo   = "CARGO";
    public const string ProdCap     = "CAP";
    public const string ProdMat     = "MAT";
}
