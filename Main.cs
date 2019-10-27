/*
 * Basic bot for Code Royale
 * Copyright Jesper Larsson 2019 @ Linköping, Sweden
 * :)
 */

using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;

using static GameState;
using static StaticConfig;
using static BotBehaviour;
using static Log;
using static UtilityFunctions;

/// <summary>
/// Handles unit logic
/// </summary>
public class UnitHandler
{
    public static UnitStrategy CurrentStrategy = UnitStrategy.MakeArchers;
    private const int TowerSpamDetectionThreshold = 2;
    private const int UnitSpamDetectionThreshold = 3; // Larger than this => build archers
    private const int QueenRushdownThreshold = 10; // Less than or equal => rush their queen
    private const int RenewArchersUnderHealth = 5;
    private const int RenewGiantsUnderHealth = 10;

    public static void SetUnitStrategy()
    {
        int archerCount = GetUnitCount(Owner.Friendly, UnitType.Archer, RenewArchersUnderHealth);

        // Is enemy queen low?
        if (EnemyQueenRef.Health <= QueenRushdownThreshold)
        {
            // Rush with knights
            Debug("Enemy queen is low, rushing with knights");
            CurrentStrategy = UnitStrategy.MakeKnights;
            return;
        }

        // Are we low on archers?
        if (archerCount < MinArcherUnitCount)
        {
            CurrentStrategy = UnitStrategy.MakeArchers;
            return;
        }

        // Is enemy spamming units?
        if (GetUnitCount(Owner.Enemy, null) > UnitSpamDetectionThreshold && archerCount < MaxArcherUnitCount)
        {
            CurrentStrategy = UnitStrategy.MakeArchers;
            return;
        }

        // Is enemy spamming towers?
        int towerCount = GetEnemyTowerCount();
        if (towerCount > TowerSpamDetectionThreshold && GetUnitCount(Owner.Friendly, UnitType.Giant, RenewGiantsUnderHealth) < MaxGiantUnitCount)
        {
            CurrentStrategy = UnitStrategy.MakeGiants;
            return;
        }

        // Otherwise pump knights
        CurrentStrategy = UnitStrategy.MakeKnights;
        return;
    }

    public static void ApplyUnitStrategyToQueen()
    {
        Debug("  Unit strat set to = " + Enum.GetName(typeof(UnitStrategy), CurrentStrategy));

        BotBehaviour.MaxGiantRax = InitialMaxGiantRax;
        BotBehaviour.MaxKnightsRax = InitialMaxKnightsRax;
        BotBehaviour.MaxArcherRax = InitialMaxArcherRax;

        // Order a rax to be built if unavailble
        if (CurrentStrategy == UnitStrategy.MakeKnights)
        {
            BotBehaviour.MaxKnightsRax += AllowdAdditionalKnightsRax;
        }
        else if (CurrentStrategy == UnitStrategy.MakeArchers)
        {
            BotBehaviour.MaxArcherRax += AllowdAdditionalArcherRax;
        }
        else if (CurrentStrategy == UnitStrategy.MakeGiants)
        {
            BotBehaviour.MaxGiantRax += AllowdAdditionalGiantRax;
        }
        else
        {
            Debug("WARNING - Unknown strategy set");
            SanityCheckFailed();
        }
    }

    private static int GetEnemyTowerCount()
    {
        int count = 0;
        foreach (var iter in Sites)
        {
            if (iter.Owner != Owner.Enemy)
                continue;
            if (iter.Type != StructureType.Tower)
                continue;
            count++;
        }
        return count;
    }

    public static void DetermineTrainAction()
    {
        // Determine unit type to train depending on current strategy
        int targetRaxSubType = -1;
        int targetUnitCost = -1;
        if (CurrentStrategy == UnitStrategy.MakeArchers)
        {
            targetRaxSubType = (int)UnitType.Archer;
            targetUnitCost = ArcherCost;
        }
        else if (CurrentStrategy == UnitStrategy.MakeKnights)
        {
            targetRaxSubType = (int)UnitType.Knight;
            targetUnitCost = KnightCost;
        }
        else if (CurrentStrategy == UnitStrategy.MakeGiants)
        {
            targetRaxSubType = (int)UnitType.Giant;
            targetUnitCost = GiantCost;
        }
        else
        {
            Debug("Unsupported unit type");
            SanityCheckFailed();
        }

        // Train
        var sitesToTrainAt = new List<Site>();
        bool canAffordTargetUnit = GoldAvailable >= targetUnitCost;
        bool canAffordAdditionalKnights = GoldAvailable >= (targetUnitCost + KnightCost);

        // Find a site for target unit if possible
        if (canAffordTargetUnit)
        {
            Site targetRax = FindAvailableRaxOfType(targetRaxSubType);
            if (targetRax != null)
            {
                Debug("Building target unit type " + targetRaxSubType);
                sitesToTrainAt.Add(targetRax);
                GoldAvailable -= targetUnitCost;
            }
        }

        // Also buy knight if we can
        if (canAffordAdditionalKnights)
        {
            Site knightRax = FindAvailableRaxOfType((int)UnitType.Knight);
            if (knightRax != null)
            {
                Debug("Building additional knight because we have spare gold");
                sitesToTrainAt.Add(knightRax);
                GoldAvailable -= KnightCost;
            }
        }

        // Send command
        string idList = "";
        foreach (var iter in sitesToTrainAt)
            idList += " " + iter.SiteId;
        Command("TRAIN" + idList);
    }

    private static Site FindAvailableRaxOfType(int subType)
    {
        for (int index = 0; index < SiteCount; index++)
        {
            Site siteIter = Sites[index];

            if (siteIter.Type == StructureType.Barracks &&
                siteIter.Owner == Owner.Friendly &&
                siteIter.RangeOrType == subType &&
                siteIter.CooldownOrHealthOrIncome == 0)
            {
                return siteIter;
            }
        }

        return null;
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
        if (CurrentGameTick == 0)
        {
            Init();
        }

        if (QueenTouchedSiteOrNull != null && QueenTouchedSiteOrNull.Owner == Owner.None)
        {
            ConstructBuilding();
        }
        else if (QueenTouchedSiteOrNull != null && QueenTouchedSiteOrNull.Owner == Owner.Friendly &&
            QueenTouchedSiteOrNull.Type == StructureType.GoldMine && QueenTouchedSiteOrNull.TargetType == StructureType.GoldMine &&
            QueenTouchedSiteOrNull.CooldownOrHealthOrIncome < QueenTouchedSiteOrNull.MaxMiningRate
            )
        {
            UpgradeMine();
        }
        else if (QueenTouchedSiteOrNull != null && QueenTouchedSiteOrNull.Owner == Owner.Friendly &&
            QueenTouchedSiteOrNull.Type == StructureType.Tower && QueenTouchedSiteOrNull.TargetType == StructureType.Tower &&
            QueenTouchedSiteOrNull.CooldownOrHealthOrIncome <= UpgradeThresholdHealth)
        {
            UpgradeTower();
        }
        else
        {
            QueenMainLoop();
        }
    }

    private static void QueenMainLoop()
    {
        // Build initial/replacement mines
        int activeMines = GetActiveMineCount();
        if (activeMines < MinMineCount)
        {
            Site mineLocation = FindMineLocation();

            if (mineLocation != null)
            {
                mineLocation.TargetType = StructureType.GoldMine;

                Debug("COMMAND - Moving to build priority mine at " + mineLocation);
                Command($"MOVE {mineLocation.Location.x} {mineLocation.Location.y}");
                return;
            }
            else
            {
                Debug("WARNING: Unable to find a gold mine location");
            }
        }

        // Move towards planned center tower
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

        // Build extra mines
        if (activeMines < MaxMineCount)
        {
            Site mineLocation = FindMineLocation();

            if (mineLocation != null)
            {
                mineLocation.TargetType = StructureType.GoldMine;

                Debug("COMMAND - Moving to build extra mine at " + mineLocation);
                Command($"MOVE {mineLocation.Location.x} {mineLocation.Location.y}");
                return;
            }
            else
            {
                Debug("WARNING: Unable to find a gold mine location");
            }
        }

        // Find other tower location if it's safe and we have already upgraded our central tower
        if (CentralTowerUpgradesPerformed < CentralTowerTargetUpgrades)
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

        // Kite enemy knights if necessary
        Point? kiteLocation = DoWeNeedToKiteEnemyUnits();
        if (kiteLocation.HasValue)
        {
            Debug("COMMAND - Kiting enemy knights towards " + kiteLocation.Value);
            Command($"MOVE {kiteLocation.Value.X} {kiteLocation.Value.Y}");
            return;
        }

        // Does a tower need ugprading / repair?
        Site towerToUpgrade = FindSafeTowerThatNeedsUpgrading();
        if (towerToUpgrade != null)
        {
            Debug("COMMAND - Moving to upgrade tower " + towerToUpgrade);
            Command($"MOVE {towerToUpgrade.Location.x} {towerToUpgrade.Location.y}");
            return;
        }

        // Fallback strategy, we have nothing to do - Find safest spot and stay there
        Point safestPoint = FindGeneralSafeSpotForQueen();
        Debug("COMMAND - Moving to safespot at " + safestPoint);
        Command($"MOVE {safestPoint.X} {safestPoint.Y}");
        return;
    }

    private static Site FindMineLocation()
    {
        // Action timeout prevents us from rebuilding the same mine that just got destroyed
        Site location = null;
        double minDistance = double.MaxValue;

        // Search safe sites first
        for (int index = 0; index < SitesOrderedByInitialRange.Length && index < TargetCentralSiteIndex; index++)
        {
            Site iter = SitesOrderedByInitialRange[index];
            if (iter.Owner != Owner.None)
                continue;
            if (iter.Type != StructureType.None)
                continue;
            if (IsInRangeOfEnemyTowers(iter.Location))
                continue;
            if (iter.OnActionCooldownUntilTick > CurrentGameTick)
                continue;
            if (iter.GoldRemaining <= MinGoldAvailableThreshold)
                continue;

            double distance = iter.Location.GetDistanceTo(QueenRef.Location);
            if (distance < minDistance)
            {
                minDistance = distance;
                location = iter;
            }
        }

        // Then less safe sites
        for (int index = 0; index < SitesOrderedByInitialRange.Length; index++)
        {
            Site iter = SitesOrderedByInitialRange[index];
            if (iter.Owner != Owner.None)
                continue;
            if (iter.Type != StructureType.None)
                continue;
            if (IsInRangeOfEnemyTowers(iter.Location))
                continue;
            if (iter.OnActionCooldownUntilTick > CurrentGameTick)
                continue;
            if (iter.GoldRemaining <= MinGoldAvailableThreshold)
                continue;

            double distance = iter.Location.GetDistanceTo(QueenRef.Location);
            if (distance < minDistance)
            {
                minDistance = distance;
                location = iter;
            }
        }

        return location;
    }

    private static int GetActiveMineCount()
    {
        int count = 0;
        foreach (var iter in Sites)
        {
            if (iter.Owner != Owner.Friendly)
                continue;
            if (iter.Type != StructureType.GoldMine)
                continue;
            if (iter.GoldRemaining <= MinGoldAvailableThreshold)
                continue;

            count++;
        }

        return count;
    }

    private static void UpgradeMine()
    {
        Debug("COMMAND - Upgrading touched mine");
        Command($"BUILD {QueenTouchedSiteOrNull.SiteId} MINE");
    }

    private static void UpgradeTower()
    {
        Debug("COMMAND - Upgrading touched tower");
        Command($"BUILD {QueenTouchedSiteOrNull.SiteId} TOWER");
    }

    private static Site FindSafeTowerThatNeedsUpgrading()
    {
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
            if (distanceToCentralTower > CentralTower.RangeOrType)
                continue;

            if (iter.CooldownOrHealthOrIncome < UpgradeThresholdHealth)
                return iter;
        }

        return null;
    }

    /// <summary>
    /// Null = do not need to kite
    /// Otherwise a point towards which to move
    /// </summary>
    private static Point? DoWeNeedToKiteEnemyUnits()
    {
        // More than this and we starting kiting
        const int KiteCountThreshold = 2;
        // Knight must be within this range
        const int KiteDistanceThreshold = 500;

        // Get number of knights which are close to us
        int count = 0;
        foreach (var iter in Units)
        {
            if (iter.Owner != Owner.Enemy)
                continue;
            if (iter.Type != UnitType.Knight)
                continue;

            double distance = iter.Location.GetDistanceTo(QueenRef.Location);
            if (distance <= KiteDistanceThreshold)
                count++;
        }

        if (count < KiteCountThreshold)
            return null;

        // Start kiting
        return new Point(QueenStartedAt.x, QueenStartedAt.y);
    }

    private static Point FindGeneralSafeSpotForQueen()
    {
        return new Point(CentralTower.Location.x, CentralTower.Location.y);

        // Does not work properly:
        //// Find a point where we're inside more than central tower circle
        //var candidatePoints = new List<Point>();
        //foreach (var iter in Sites)
        //{
        //    if (iter.Owner != Owner.Friendly)
        //        continue;
        //    if (iter.Type != StructureType.Tower)
        //        continue;

        //    int intersectionCount = FindCircleCircleIntersections(
        //        iter.Location.x,
        //        iter.Location.y,
        //        iter.RangeOrType,
        //        CentralTower.Location.x,
        //        CentralTower.Location.y,
        //        CentralTower.RangeOrType,
        //        out Point point1,
        //        out Point point2
        //        );

        //    if (intersectionCount == 0)
        //        continue;

        //    candidatePoints.Add(point1);
        //}

        //if (candidatePoints.Count == 0)
        //    return new Point(CentralTower.Location.x, CentralTower.Location.y);

        //// Check if multiple overlaps are possible
        //int maxCount = -1;
        //Point maxPoint = new Point();
        //foreach (Point iter in candidatePoints)
        //{
        //    int overlapCount = GetTowerOverlapCountForPoint(iter);
        //    if (overlapCount > maxCount)
        //    {
        //        maxCount = overlapCount;
        //        maxPoint = iter;
        //    }
        //}
        //return maxPoint;
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
        StructureType structType;
        UnitType raxType;
        if (QueenTouchedSiteOrNull.TargetType == StructureType.Tower)
        {
            // Higher layer ordered a tower
            structType = StructureType.Tower;
            raxType = UnitType.None;
        }
        else if (QueenTouchedSiteOrNull.TargetType == StructureType.Barracks)
        {
            // Higher layer ordered a specific building
            Debug("COMMAND - Building target building");
            structType = StructureType.Barracks;
            raxType = QueenTouchedSiteOrNull.TargetRaxType;
        }
        else if (QueenTouchedSiteOrNull.TargetType == StructureType.GoldMine)
        {
            QueenTouchedSiteOrNull.OnActionCooldownUntilTick = CurrentGameTick + MineActionTimeoutTicks;
            structType = StructureType.GoldMine;
            raxType = UnitType.None;
        }
        else
        {
            // We just happend to touch something on the way somewhere
            Debug($"COMMAND - Happened to touch site " + QueenTouchedSiteOrNull + " on the way");
            UnitType requestedRaxType = DetermineBarracksTypeOrNone();

            // Set auto mode if we don't need a rax
            if (requestedRaxType == UnitType.None)
            {
                structType = StructureType.None;
                raxType = UnitType.None;
            }
            else
            {
                structType = StructureType.Barracks;
                raxType = requestedRaxType;
            }
        }

        // Send build command
        if (structType == StructureType.Tower)
        {
            Command($"BUILD {touchedId} TOWER");
            return;
        }
        else if (structType == StructureType.GoldMine)
        {
            Command($"BUILD {touchedId} MINE");
            return;
        }
        else if (structType == StructureType.None)
        {
            // Auto pick something, TODO
            Command($"BUILD {touchedId} MINE");
            return;
        }

        // Send build command - With barracks subtype
        if (structType != StructureType.Barracks)
        {
            Debug("Invalid build order");
            SanityCheckFailed();
        }
        if (raxType == UnitType.Knight)
        {
            Command($"BUILD {touchedId} BARRACKS-KNIGHT");
            return;
        }
        else if (raxType == UnitType.Giant)
        {
            Command($"BUILD {touchedId} BARRACKS-GIANT");
            return;
        }
        else if (raxType == UnitType.Archer)
        {
            Command($"BUILD {touchedId} BARRACKS-ARCHER");
            return;
        }
        else
        {
            Debug("WARNING - Cant build rax type");
            SanityCheckFailed();
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

        if (archerCount < MaxArcherRax)
            return UnitType.Archer;
        if (knightCount < MaxKnightsRax)
            return UnitType.Knight;
        if (giantCount < MaxGiantRax)
            return UnitType.Giant;

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

    private static void Init()
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

            UnitHandler.SetUnitStrategy();
            UnitHandler.ApplyUnitStrategyToQueen();
            UnitHandler.DetermineTrainAction();

            CurrentGameTick++;
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
        GoldAvailable = gold;

        int touchedSiteId = int.Parse(ReadBuffer[1]); // -1 if none

        // Read - Site states
        for (int i = 0; i < GameState.SiteCount; i++)
        {
            ReadBuffer = Console.ReadLine().Split(' ');
            int siteId = int.Parse(ReadBuffer[0]);
            int goldRemaining = int.Parse(ReadBuffer[1]); // -1 = unknown
            int maxMiningRate = int.Parse(ReadBuffer[2]); // -1 = unknown
            int structureType = int.Parse(ReadBuffer[3]);
            int owner = int.Parse(ReadBuffer[4]); ;

            // Guessed from input data
            int param1 = int.Parse(ReadBuffer[5]);
            int param2 = int.Parse(ReadBuffer[6]);

            Sites[siteId].Type = (StructureType)structureType;
            Sites[siteId].CooldownOrHealthOrIncome = param1; // cd for rax, health for towers, income rate for mines
            Sites[siteId].Owner = (Owner)owner;
            Sites[siteId].RangeOrType = param2; // range for towers, unit type for rax
            Sites[siteId].MaxMiningRate = maxMiningRate;
            Sites[siteId].GoldRemaining = goldRemaining;
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
            int health = int.Parse(ReadBuffer[4]);

            Units[i] = new Unit();
            Units[i].Owner = (Owner)owner;
            Units[i].Type = (UnitType)unitType;
            Units[i].Location = new MapCoordinate(x, y);
            Units[i].Health = health;

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
        }

        // Read - Queen touching site
        if (touchedSiteId == -1)
        {
            QueenTouchedSiteOrNull = null;
        }
        else
        {
            Debug("  Queen touching site " + touchedSiteId);
            QueenTouchedSiteOrNull = Sites[touchedSiteId];
        }

        // Debug info
        if (CurrentGameTick == 0)
        {
            QueenStartedAt = QueenRef.Location;
            Debug("Our queen starts at " + QueenStartedAt);
            Debug("Enemy queen starts at " + EnemyQueenRef.Location);
        }
    }
}

public static class StaticConfig
{
    public const int KnightCost = 80;
    public const int ArcherCost = 100;
    public const int GiantCost = 140;
}

/// <summary>
/// Bot behaviour settings
/// </summary>
public static class BotBehaviour
{
    public const int UpgradeThresholdHealth = 400;
    public const int MinGoldAvailableThreshold = 10;
    public const int MineActionTimeoutTicks = 10; // Number of ticks before a mine can be rebuilt

    public const int MinMineCount = 3;
    public const int MaxMineCount = 6;

    public const int MaxGiantUnitCount = 1;
    public const int MaxArcherUnitCount = 4;
    public const int MinArcherUnitCount = 2;
    //public const int MaxKnightUnitCount = int.MaxValue;

    public const int InitialMaxGiantRax = 0;
    public const int InitialMaxKnightsRax = 1;
    public const int InitialMaxArcherRax = 1;

    public const int AllowdAdditionalGiantRax = 1;
    public const int AllowdAdditionalKnightsRax = 0;
    public const int AllowdAdditionalArcherRax = 0;

    public static int MaxGiantRax = InitialMaxGiantRax;
    public static int MaxKnightsRax = InitialMaxKnightsRax;
    public static int MaxArcherRax = InitialMaxArcherRax;

    // Towers cannot be upgraded past this point
    public const int CentralTowerTargetUpgrades = 5; // max = 7
}

public static class GameState
{
    public static int CurrentGameTick = 0;

    // Sites, index equals id, index 0 = id 0 etc
    public static Site[] Sites;
    public static int SiteCount;

    // Units
    public static Unit[] Units;
    public static int UnitCount;

    // Queen
    public static Unit QueenRef;
    public static MapCoordinate QueenStartedAt;
    public static Unit EnemyQueenRef;
    public static Site QueenTouchedSiteOrNull = null;

    public static int GoldAvailable = 100;
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

    public static void SanityCheckFailed()
    {
        Debug("WARNING - Sanity check failed");
        Command("SANITYCHECKFAILED");
    }
}

public enum UnitStrategy
{
    MakeKnights = 0,
    MakeArchers = 1,
    MakeGiants = 2
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
    GoldMine = 0,
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
    public int GoldRemaining;
    public int MaxMiningRate;
    public MapCoordinate Location;

    // Current type and planned type
    public StructureType Type = StructureType.None;
    public StructureType TargetType = StructureType.None;
    public UnitType TargetRaxType = UnitType.None;
    public Owner Owner = Owner.None;

    /// <summary>
    /// Next action can be taken at
    /// </summary>
    public int OnActionCooldownUntilTick = 0;

    /// <summary>
    /// Rax = -1 = cannot build, 0 = can build, positive integer = cooldown left
    /// Tower = Health
    /// Mine = Income
    /// </summary>
    public int CooldownOrHealthOrIncome;

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
    public int Health = 0;

    public override string ToString()
    {
        string type = Enum.GetName(typeof(UnitType), this.Type);
        return $"[Unit {type}-{Location.ToString()}";
    }
}

public static class UtilityFunctions
{
    public static int GetUnitCount(Owner? targetOwner, UnitType? type, int? ignoreUnitsUnderHealthThreshold = null)
    {
        int count = 0;
        foreach (var iter in Units)
        {
            if (targetOwner.HasValue && iter.Owner != targetOwner)
                continue;
            if (type.HasValue && iter.Type != type)
                continue;
            if (ignoreUnitsUnderHealthThreshold.HasValue && iter.Health < ignoreUnitsUnderHealthThreshold.Value)
                continue;


            count++;
        }
        return count;
    }
}