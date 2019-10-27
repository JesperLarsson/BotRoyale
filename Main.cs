﻿/*
 * Basic bot for Code Royale
 * Copyright Jesper Larsson 2019 @ Linköping, Sweden
 * :)
 */

using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;

using static GameState;
using static StaticConfig;
using static BehaviourConfig;
using static Log;
using System.Drawing;

/// <summary>
/// Handles unit logic
/// </summary>
public class UnitHandler
{
    public static void DetermineTrainAction()
    {
        Site siteToTrainAt = null;

        if (GoldAvailable >= KnightCost)
        {
            for (int index = 0; index < SiteCount; index++)
            {
                Site siteIter = Sites[index];

                if (siteIter.Type == StructureType.Barracks &&
                    siteIter.Owner == Owner.Friendly &&
                    siteIter.CooldownOrHealth == 0)
                {
                    siteToTrainAt = siteIter;
                    break;
                }
            }
        }

        if (siteToTrainAt == null)
        {
            Command("TRAIN");
        }
        else
        {
            Command("TRAIN " + siteToTrainAt.SiteId);
            GoldAvailable -= KnightCost;
        }
    }
}

/// <summary>
/// Handles building logic and queen movement
/// </summary>
public static class QueenHandler
{
    // We build around this central tower for safety
    private static Site CentralTower;
    private static int CentralTowerUpgradesPerformed = 0;

    // Sites by distance from the starting point, all points lower than the central index are considered "safe"
    private static Site[] SitesOrderedByInitialRange;
    private static int TargetCentralSiteIndex = -1;

    public static void DetermineQueenAction()
    {
        if (IsFirstTurn)
            FirstTurnInit();

        if (QueenTouchedSiteOrNull != null && QueenTouchedSiteOrNull.Owner == Owner.None)
        {
            ConstructBuilding();
        }
        else
        {
            MoveQueen();
        }
    }

    private static void MoveQueen()
    {
        // Move towards center tile
        if (CentralTower.Owner == Owner.None)
        {
            CentralTower.TargetType = StructureType.Tower;

            Debug("COMMAND - Moving to central tower at " + CentralTower);
            Command($"MOVE {CentralTower.Location.x} {CentralTower.Location.y}");
            return;
        }

        // Build rax if we want to
        var wantedRax = DetermineBarracksTypeOrNone();
        if (wantedRax != UnitType.None)
        {
            Site targetSite = FindClosestUnclaimedSafeSite();
            targetSite.TargetType = StructureType.Barracks;
            targetSite.TargetRaxType = wantedRax;

            Debug("COMMAND - Moving to build rax at " + targetSite);
            Command($"MOVE {targetSite.Location.x} {targetSite.Location.y}");
            return;
        }

        // Find other tower location if it's safe and we have already upgraded our central tower
        if (CentralTowerUpgradesPerformed < MaxTowerUpgrades)
        {
            // Move to tower if necessary
            if (QueenTouchedSiteOrNull != CentralTower)
            {
                Debug("COMMAND - Falling back to central tower at " + CentralTower);
                Command($"MOVE {CentralTower.Location.x} {CentralTower.Location.y}");
                return;
            }

            // Start upgrading tower
            Debug("COMMAND - Upgrading central tower at " + CentralTower);
            Command($"BUILD {CentralTower.SiteId} TOWER");
            CentralTowerUpgradesPerformed++;
            return;
        }

        // Build additional support towers
        Site additionalLocation = FindAdditionalSafeTowerLocationOrNull();
        if (additionalLocation != null)
        {
            additionalLocation.TargetType = StructureType.Tower;
            Debug("COMMAND - Moving to build additional support tower at " + additionalLocation);
            Command($"MOVE {additionalLocation.Location.x} {additionalLocation.Location.y}");
            return;
        }

        // Fallback strategy, we have nothing to do - Find safest spot and stay there
        Point safestPoint = FindSafestSpotForQueen();
        Debug("COMMAND - Moving to safespot at " + safestPoint);
        Command($"MOVE {safestPoint.X} {safestPoint.Y}");
        return;
    }

    private static Point FindSafestSpotForQueen()
    {
        // Find a point where we're inside more than central tower circle
        var candidatePoints = new List<Point>();
        foreach (var iter in Sites)
        {
            if (iter.Owner != Owner.Friendly)
                continue;
            if (iter.Type != StructureType.Tower)
                continue;

            int intersectionCount = FindCircleCircleIntersections(
                iter.Location.x,
                iter.Location.y,
                iter.RangeOrType,
                CentralTower.Location.x,
                CentralTower.Location.y,
                CentralTower.RangeOrType,
                out Point point1,
                out Point point2
                );

            if (intersectionCount == 0)
                continue;

            candidatePoints.Add(point1);
        }

        if (candidatePoints.Count == 0)
            return new Point(CentralTower.Location.x, CentralTower.Location.y);

        // Check if multiple overlaps are possible
        int maxCount = -1;
        Point maxPoint = new Point();
        foreach (Point iter in candidatePoints)
        {
            int overlapCount = GetTowerOverlapCountForPoint(iter);
            if (overlapCount > maxCount)
            {
                maxCount = overlapCount;
                maxPoint = iter;
            }
        }
        return maxPoint;
    }

    /// <summary>
    /// Find a spot to build a tower that is within range of another tower
    /// </summary>
    private static Site FindAdditionalSafeTowerLocationOrNull()
    {
        // Check locations closer to our spawn first
        for (int index = 0; index < SitesOrderedByInitialRange.Length && index < TargetCentralSiteIndex; index++)
        {
            Site iter = SitesOrderedByInitialRange[index];
            if (iter.Owner != Owner.None)
                continue;
            if (iter.Type != StructureType.None)
                continue;
            if (IsInRangeOfEnemyTowers(iter.Location))
                continue;

            double distanceToCentralTower = CentralTower.Location.GetDistanceTo(iter.Location);
            if (distanceToCentralTower <= CentralTower.RangeOrType)
                return iter;
        }

        // Then any location
        for (int index = 0; index < SitesOrderedByInitialRange.Length; index++)
        {
            Site iter = SitesOrderedByInitialRange[index];
            if (iter.Owner != Owner.None)
                continue;
            if (iter.Type != StructureType.None)
                continue;
            if (IsInRangeOfEnemyTowers(iter.Location))
                continue;

            double distanceToCentralTower = CentralTower.Location.GetDistanceTo(iter.Location);
            if (distanceToCentralTower <= CentralTower.RangeOrType)
                return iter;
        }

        return null;
    }

    private static bool IsInRangeOfEnemyTowers(MapCoordinate location)
    {
        foreach (var iter in Sites)
        {
            if (iter.Owner != Owner.Enemy)
                continue;
            if (iter.Type != StructureType.Tower)
                continue;

            double distance = iter.Location.GetDistanceTo(location);
            if (distance <= iter.RangeOrType)
                return true;
        }

        return false;
    }

    private static void ConstructBuilding()
    {
        int touchedId = QueenTouchedSiteOrNull.SiteId;

        // Determine type to build
        UnitType raxType;
        if (QueenTouchedSiteOrNull.TargetType == StructureType.Tower)
        {
            // Higher layer ordered a tower
            raxType = UnitType.None;
        }
        else if (QueenTouchedSiteOrNull.TargetType == StructureType.Barracks)
        {
            // Higher layer ordered a specific building
            Debug("COMMAND - Building target building");
            raxType = QueenTouchedSiteOrNull.TargetRaxType;
        }
        else
        {
            // We just happend to touch something on the way somewhere
            Debug($"COMMAND - Happened to touch site " + QueenTouchedSiteOrNull + " on the way");
            raxType = DetermineBarracksTypeOrNone();
        }

        // Send build command
        if (raxType == UnitType.Knight)
        {
            Command($"BUILD {touchedId} BARRACKS-KNIGHT");
        }
        else if (raxType == UnitType.Giant)
        {
            Command($"BUILD {touchedId} BARRACKS-GIANT");
        }
        else if (raxType == UnitType.Archer)
        {
            Command($"BUILD {touchedId} BARRACKS-ARCHER");
        }
        else if (raxType == UnitType.None)
        {
            Command($"BUILD {touchedId} TOWER");
        }
        else
        {
            Debug("Cant build building type");
            Command("SANITYCHECKFAILED");
        }
    }

    private static UnitType DetermineBarracksTypeOrNone()
    {
        // Get number of current rax of each type
        int knightCount = 0;
        int giantCount = 0;
        int archerCount = 0;
        foreach (var site in Sites)
        {
            if (site.Owner != Owner.Friendly)
                continue;
            if (site.Type != StructureType.Barracks)
                continue;

            var raxType = ((UnitType)site.RangeOrType);
            if (raxType == UnitType.Knight)
                knightCount++;
            if (raxType == UnitType.Giant)
                giantCount++;
            if (raxType == UnitType.Archer)
                archerCount++;
        }

        if (giantCount < StartingMaxGiantRax)
            return UnitType.Giant;
        if (knightCount < StartingMaxKnightsRax)
            return UnitType.Knight;
        if (archerCount < StartingMaxArcherRax)
            return UnitType.Archer;

        // We don't want a rax
        return UnitType.None;
    }

    public static Site FindClosestUnclaimedSafeSite()
    {
        // It's safer to "fall back" towards our spawn
        double minDistance = double.MaxValue;
        Site minSite = null;
        for (int index = 0; index < SitesOrderedByInitialRange.Length && index < TargetCentralSiteIndex; index++)
        {
            Site iter = SitesOrderedByInitialRange[index];

            if (iter.Owner != Owner.None)
                continue; // Someone already owns it

            double distance = iter.Location.GetDistanceTo(QueenRef.Location);
            if (distance < minDistance)
            {
                minDistance = distance;
                minSite = iter;
            }
        }

        return minSite;
    }

    private static void FirstTurnInit()
    {
        // Find target site for our central tower
        //   Furthest possible tower that we can reach before the enemy queen
        var enemyQueenLocation = EnemyQueenRef.Location;
        var queenLocation = QueenRef.Location;
        double maxDistance = double.MinValue;
        var sitesByDistance = new List<Site>();

        foreach (Site siteIter in Sites)
        {
            double ourDistanceToSite = siteIter.Location.GetDistanceTo(queenLocation);
            double enemyDistanceToSite = siteIter.Location.GetDistanceTo(enemyQueenLocation);

            siteIter.DistanceFromInitialStart = ourDistanceToSite;
            sitesByDistance.Add(siteIter);

            if (enemyDistanceToSite < ourDistanceToSite)
                continue;

            if (ourDistanceToSite > maxDistance)
            {
                maxDistance = ourDistanceToSite;
                CentralTower = siteIter;
            }
        }

        // Sort list of sites by distance
        sitesByDistance.Sort(delegate (Site c1, Site c2) { return c1.DistanceFromInitialStart.CompareTo(c2.DistanceFromInitialStart); });
        SitesOrderedByInitialRange = sitesByDistance.ToArray();

        // Find central site in sorted data
        for (int index = 0; index < SitesOrderedByInitialRange.Length; index++)
        {
            if (SitesOrderedByInitialRange[index] == CentralTower)
            {
                TargetCentralSiteIndex = index;
                break;
            }
        }

        Debug("Found target center site: " + CentralTower);
    }

    private static int GetTowerOverlapCountForPoint(PointF point)
    {
        int count = 0;

        foreach (var iter in Sites)
        {
            if (iter.Owner != Owner.Friendly)
                continue;
            if (iter.Type != StructureType.Tower)
                continue;

            var temp = new MapCoordinate((int)point.X, (int)point.Y);
            var distance = temp.GetDistanceTo(iter.Location);
            if (distance <= iter.RangeOrType)
                count++;
        }

        return count;
    }

    private static int FindCircleCircleIntersections(
        float cx0, float cy0, float radius0,
        float cx1, float cy1, float radius1,
        out Point intersection1, out Point intersection2)
    {
        // Taken from https://forum.unity.com/threads/calulate-the-intersection-points-of-two-circles.611032/

        // Find the distance between the centers.
        float dx = cx0 - cx1;
        float dy = cy0 - cy1;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // See how many solutions there are.
        if (dist > radius0 + radius1)
        {
            // No solutions, the circles are too far apart.
            intersection1 = new Point(0, 0);
            intersection2 = new Point(0, 0);
            return 0;
        }
        else if (dist < Math.Abs(radius0 - radius1))
        {
            // No solutions, one circle contains the other.
            intersection1 = new Point(0, 0);
            intersection2 = new Point(0, 0);
            return 0;
        }
        else if ((dist == 0) && (radius0 == radius1))
        {
            // No solutions, the circles coincide.
            intersection1 = new Point(0, 0);
            intersection2 = new Point(0, 0);
            return 0;
        }
        else
        {
            // Find a and h.
            double a = (radius0 * radius0 -
                radius1 * radius1 + dist * dist) / (2 * dist);
            double h = Math.Sqrt(radius0 * radius0 - a * a);

            // Find P2.
            double cx2 = cx0 + a * (cx1 - cx0) / dist;
            double cy2 = cy0 + a * (cy1 - cy0) / dist;

            // Get the points P3.
            intersection1 = new Point(
                (int)(cx2 + h * (cy1 - cy0) / dist),
                (int)(cy2 - h * (cx1 - cx0) / dist));
            intersection2 = new Point(
                (int)(cx2 - h * (cy1 - cy0) / dist),
                (int)(cy2 + h * (cx1 - cx0) / dist));

            // See if we have 1 or 2 solutions.
            if (dist == radius0 + radius1) return 1;
            return 2;
        }
    }

}

/// <summary>
/// Main loop
/// </summary>
public class MainLoop
{
    private static string[] ReadBuffer;

    /// <summary>
    /// App entry point
    /// </summary>
    static void Main(string[] args)
    {
        Init();

        while (true)
        {
            ReadTurnInputs();

            QueenHandler.DetermineQueenAction();
            UnitHandler.DetermineTrainAction();

            GoldAvailable += 10;
            IsFirstTurn = false;
        }
    }

    static void Init()
    {
        Debug("=====================");
        Debug("START AT" + DateTime.Now);

        GameState.SiteCount = int.Parse(Console.ReadLine());
        GameState.Sites = new Site[GameState.SiteCount];

        for (int i = 0; i < GameState.SiteCount; i++)
        {
            ReadBuffer = Console.ReadLine().Split(' ');
            int siteId = int.Parse(ReadBuffer[0]);
            int x = int.Parse(ReadBuffer[1]);
            int y = int.Parse(ReadBuffer[2]);
            int radius = int.Parse(ReadBuffer[3]);

            GameState.Sites[i] = new Site(siteId, radius, new MapCoordinate(x, y));
        }
    }

    private static void ReadTurnInputs()
    {
        ReadBuffer = Console.ReadLine().Split(' ');

        int gold = int.Parse(ReadBuffer[0]);
        int touchedSiteId = int.Parse(ReadBuffer[1]); // -1 if none

        // Read - Site states
        for (int i = 0; i < GameState.SiteCount; i++)
        {
            ReadBuffer = Console.ReadLine().Split(' ');
            int siteId = int.Parse(ReadBuffer[0]);
            int structureType = int.Parse(ReadBuffer[3]);
            int owner = int.Parse(ReadBuffer[4]); ;

            // Guessed from input data
            int param1 = int.Parse(ReadBuffer[5]);
            int param2 = int.Parse(ReadBuffer[6]);

            Sites[siteId].Type = (StructureType)structureType;
            Sites[siteId].CooldownOrHealth = param1; // cd for rax, health for towers
            Sites[siteId].Owner = (Owner)owner;
            Sites[siteId].RangeOrType = param2; // range for towers, unit type for rax
        }

        // Read - Unit states
        int numUnits = int.Parse(Console.ReadLine());
        Units = new Unit[numUnits];
        for (int i = 0; i < numUnits; i++)
        {
            ReadBuffer = Console.ReadLine().Split(' ');
            int x = int.Parse(ReadBuffer[0]);
            int y = int.Parse(ReadBuffer[1]);
            int owner = int.Parse(ReadBuffer[2]);
            int unitType = int.Parse(ReadBuffer[3]); // -1 = QUEEN, 0 = KNIGHT, 1 = ARCHER

            Units[i] = new Unit();
            Units[i].Owner = (Owner)owner;
            Units[i].Type = (UnitType)unitType;
            Units[i].Location = new MapCoordinate(x, y);

            // Update queen position
            if (unitType == (int)UnitType.Queen)
            {
                if (Units[i].Owner == Owner.Enemy)
                {
                    EnemyQueenRef = Units[i];
                }
                else
                {
                    QueenRef = Units[i];
                }
            }
            //int health = int.Parse(inputs[4]);
        }

        // Read - Queen touching site
        if (touchedSiteId == -1)
        {
            QueenTouchedSiteOrNull = null;
        }
        else
        {
            Debug("  Queen is touching site " + touchedSiteId);
            QueenTouchedSiteOrNull = Sites[touchedSiteId];
        }

        // Debug info
        if (IsFirstTurn)
        {
            Debug("Our queen starts at " + QueenRef.Location);
            Debug("Enemy queen starts at " + EnemyQueenRef.Location);
        }
    }
}

public static class StaticConfig
{
    public const int KnightCost = 80;
    public const int GiantCost = 140;
}

/// <summary>
/// Bot behaviour settings
/// </summary>
public static class BehaviourConfig
{
    public const int StartingMaxGiantRax = 0;
    public const int StartingMaxKnightsRax = 1;
    public const int StartingMaxArcherRax = 1;

    public const int MaxTowerUpgrades = 7;


}

public static class GameState
{
    // Sites, index equals id, index 0 = id 0 etc
    public static Site[] Sites;
    public static int SiteCount;

    // Units
    public static Unit[] Units;
    public static int UnitCount;

    // Queen
    public static Unit QueenRef;
    public static Unit EnemyQueenRef;
    public static Site QueenTouchedSiteOrNull = null;

    public static int GoldAvailable = 100;
    public static bool IsFirstTurn = true;
}

public static class Log
{
    public static void Command(string arg)
    {
        Debug("== RUNNING COMMAND: " + arg);
        Console.WriteLine(arg);
    }

    public static void Debug(string arg)
    {
        Console.Error.WriteLine(arg);
    }
}

public enum UnitType
{
    None = -99,

    Queen = -1,
    Knight = 0,
    Archer = 1,
    Giant = 2,
}

public enum StructureType
{
    None = -1,
    Tower = 1,
    Barracks = 2,
}

public enum Owner
{
    None = -1,
    Friendly = 0,
    Enemy = 1
}

/// <summary>
/// Map site / possible build location
/// </summary>
public class Site
{
    public int SiteId;
    public int CollisionRadius;
    public int RangeOrType;
    public MapCoordinate Location;

    // Current type and planned type
    public StructureType Type = StructureType.None;
    public StructureType TargetType = StructureType.None;
    public UnitType TargetRaxType = UnitType.None;

    public Owner Owner = Owner.None;

    // -1 = cannot build, 0 = can build, positive integer = cooldown left
    public int CooldownOrHealth;

    public double DistanceFromInitialStart = double.MinValue;

    public Site(int siteId, int radius, MapCoordinate location)
    {
        SiteId = siteId;
        CollisionRadius = radius;
        Location = location;
    }

    public override string ToString()
    {
        return $"[Site {SiteId} {Location}]";
    }
}

/// <summary>
/// Coordinate on map
/// </summary>
public class MapCoordinate
{
    public int x;
    public int y;

    public MapCoordinate(int x, int y)
    {
        this.x = x;
        this.y = y;
    }

    public double GetDistanceTo(MapCoordinate other)
    {
        return Math.Sqrt(Math.Pow((other.x - this.x), 2) + Math.Pow((other.y - this.y), 2));
    }

    public override string ToString()
    {
        return $"{x},{y}";
    }
}

public class Unit
{
    public MapCoordinate Location;
    public UnitType Type;
    public Owner Owner = Owner.None;

    public override string ToString()
    {
        string type = Enum.GetName(typeof(UnitType), this.Type);
        return $"[Unit {type}-{Location.ToString()}";
    }
}