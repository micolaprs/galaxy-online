namespace GalaxyNG.Engine.Models;

public enum OrderKind
{
    // Account
    ChangeName, ChangePassword, ChangeEmail, SetRealName, Quit,
    // Planet
    RenamePlanet, SetProduction, SetRoute, Victory, SetMapView,
    // Ship Design
    DesignShip, RenameType, EliminateType,
    // Group
    SendGroup, InterceptGroup, LoadCargo, UnloadCargo, UpgradeGroup,
    BreakGroup, ScrapGroup, ReverseGroup,
    // Fleet
    CreateFleet, SendFleet, InterceptFleet, ReverseFleet,
    JoinFleet, MergeFleet, EliminateFleet,
    // Diplomacy
    DeclareAlliance, DeclareWar, SendMessage, RequestEmail,
    // Options
    SetOption,
}

public sealed record ParsedOrder(
    OrderKind Kind,
    string[] Args,
    int LineNumber = 0
);
