// Copyright Jesper Larsson 2019 @ Linköping, Sweden :)

/*
  Output - First line: One of the following:
    WAIT Do nothing
    MOVE x y Will attempt to move towards the provided coordinates (x,y given as integers)
    BUILD {siteId} BARRACKS-{type} Will attempt to build a barracks at the indicated site. If too far away, the Queen will instead move towards the site. The type must be given as either KNIGHT or ARCHER.

  Second line: TRAIN optionally followed by a list of siteId integers to start training at. 
      

Structures
    Structures can be built at no cost. A Queen character can build a structure (using the BUILD command) on a building site that she is in contact with. The variable touchedSite indicates the identifier of the building site that she is in contact with, -1 if not.
    There is one type of structure:
        BARRACKS-{type}: A player needs to build a BARRACKS with their Queen in order to train creeps ({type} can be KNIGHT or ARCHER). Every barracks is built with the ability to train a single creep type. Barracks are available for use on the turn immediately following their construction. A barracks is destroyed if it is touched by the enemy Queen.
    If she so chooses, a Queen can replace an existing friendly structure simply by building another one in its place (unless it is a Barracks that is currently training).
 
Gold
    Each player begins with 100 gold and will gain 10 gold at the end of each turn.
 
Creeps
    In order to destroy the enemy Queen, a player will need to build creeps. Once built, creeps follow very simple behaviours (see below), and cannot be controlled by the player. Creeps will eventually age and die on their own, losing 1 HP every turn.
    There are two different creep types that can be built.

        KNIGHT units are light, fast units which attack the enemy Queen. They cost 80 gold to train a group of 4.
        ARCHER units are slower, ranged units which move toward and attack nearby enemy creeps from a short distance. They cost 100 gold to train a group of 2. Note: ARCHER units do not attack the enemy Queen!
    
Training Creeps
    A player trains creeps (using the TRAIN command) by indicating the identifiers of which sites containing a barracks they wish to train creeps at. A barracks that is already in the middle of training cannot begin training again until the current creep set is built. Also, such a barracks cannot be replaced by another structure. Examples:
        TRAIN 13 6 19 - Three barracks begin training creeps
        TRAIN 14 - One barracks begins training creeps
    When the train commands are sent, the player pays the total cost in gold, and indicated barracks will begin training the appropriate set of units. After the appointed number of turns elapses, a set of creeps emerges from the barracks and begins acting by themselves on the following turn.
    The training of creeps represent an extra mandatory command every turn. For barracks not to begin training new units, a player has to use the TRAIN command with no identifier.
 */

using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;

using static MapState;
using static StaticConfig;
using static Utility;

public static class StaticConfig
{
    public const int BoardWidth = 1920;
    public const int BoardHeight = 1000;
    public const int KnightCost = 80;
}

public static class MapState
{
    /// <summary>
    /// Index 0 = site ID 0 etc
    /// </summary>
    public static Site[] Sites;
    public static int SiteCount;

    public static QueenUnit Queen;
    public static int GoldAvailable = 100;
}

public static class Utility
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
    Queen = -1,
    Knight = 0,
    Archer = 1
}

public enum StructureType
{
    None = -1,
    Barracks = 2
}

/// <summary>
/// Map site / possible build location
/// </summary>
public class Site
{
    public int SiteId;
    public int Radius;
    public Point Location;
    public bool HasBeenTaken = false;
    public StructureType Type = StructureType.None;

    public Site(int siteId, int radius, Point location)
    {
        SiteId = siteId;
        Radius = radius;
        Location = location;
    }
}

/// <summary>
/// Coordinate on map
/// </summary>
public class Point
{
    public int x;
    public int y;

    public Point(int x, int y)
    {
        this.x = x;
        this.y = y;
    }

    public double GetDistanceTo(Point other)
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
    public Point Location;
    public UnitType Type;

    public override string ToString()
    {
        string type = Enum.GetName(typeof(UnitType), this.Type);
        return $"[Unit {type}-{Location.ToString()}";
    }
}

public class QueenUnit : Unit
{
    public Site TouchedSiteOrNull = null;

    public QueenUnit() : base()
    {
        this.Type = UnitType.Queen;
    }
}

/// <summary>
/// Main class
/// </summary>
public class Player
{
    private static string[] ReadBuffer;
    private static int LastTrainIndex = -1;

    private static void MainTick()
    {
        ReadTurnInputs();

        // Determine queen action
        if (Queen.TouchedSiteOrNull != null && Queen.TouchedSiteOrNull.HasBeenTaken == false)
        {
            // Build barracks
            Command($"BUILD {Queen.TouchedSiteOrNull.SiteId} BARRACKS-KNIGHT");
            Queen.TouchedSiteOrNull.HasBeenTaken = true;
        }
        else
        {
            // Move to closest
            Site targetSite = FindUnclaimedSite();
            Debug("COMMAND - Moving to" + targetSite.SiteId);
            Command($"MOVE {targetSite.Location.x} {targetSite.Location.y}");
        }

        // Train a batch of units
        Site siteToTrain = null;
        for (int index = (LastTrainIndex + 1); index < SiteCount; index++)
        {
            Site siteIter = Sites[index];

            if (siteIter.HasBeenTaken && siteIter.Type == StructureType.Barracks)
            {
                LastTrainIndex = index;
                siteToTrain = siteIter;
                break;
            }
        }
        if (siteToTrain == null)
        {
            for (int index = 0; index < LastTrainIndex; index++)
            {
                Site siteIter = Sites[index];

                if (siteIter.HasBeenTaken && siteIter.Type == StructureType.Barracks)
                {
                    LastTrainIndex = index;
                    siteToTrain = siteIter;
                    break;
                }
            }
        }
        if (siteToTrain == null)
            Command("TRAIN");
        else
            Command("TRAIN " + siteToTrain.SiteId);
    }

    private static Site FindUnclaimedSite()
    {
        double minDistance = double.MaxValue;
        Site minSite = null;
        foreach (var iter in Sites)
        {
            if (iter.HasBeenTaken)
                continue;

            double distance = iter.Location.GetDistanceTo(Queen.Location);
            if (distance < minDistance)
            {
                minDistance = distance;
                minSite = iter;
            }
        }

        return minSite;
    }

    private static void ReadTurnInputs()
    {
        ReadBuffer = Console.ReadLine().Split(' ');

        int gold = int.Parse(ReadBuffer[0]);
        int touchedSiteId = int.Parse(ReadBuffer[1]); // -1 if none

        // Read site states
        for (int i = 0; i < MapState.SiteCount; i++)
        {
            ReadBuffer = Console.ReadLine().Split(' ');
            int siteId = int.Parse(ReadBuffer[0]);
            int structureType = int.Parse(ReadBuffer[3]); // -1 = No structure, 2 = Barracks
            //int owner = int.Parse(ReadBuffer[4]); // -1 = No structure, 0 = Friendly, 1 = Enemy
            //int param1 = int.Parse(inputs[5]);
            //int param2 = int.Parse(inputs[6]);

            Sites[siteId].Type = (StructureType)structureType;
        }

        // Read unit states
        int numUnits = int.Parse(Console.ReadLine());
        for (int i = 0; i < numUnits; i++)
        {
            ReadBuffer = Console.ReadLine().Split(' ');
            int x = int.Parse(ReadBuffer[0]);
            int y = int.Parse(ReadBuffer[1]);
            int owner = int.Parse(ReadBuffer[2]);
            int unitType = int.Parse(ReadBuffer[3]); // -1 = QUEEN, 0 = KNIGHT, 1 = ARCHER

            // Find queen pos
            if (owner == 0 && unitType == (int)UnitType.Queen)
            {
                if (Queen == null)
                {
                    Queen = new QueenUnit();
                }
                Queen.Location = new Point(x, y);
                Debug("Queen location = " + Queen.Location);
            }

            //int health = int.Parse(inputs[4]);
        }

        // Read queen touching state
        if (touchedSiteId == -1)
        {
            Queen.TouchedSiteOrNull = null;
        }
        else
        {
            Debug("Touching site " + touchedSiteId);
            Queen.TouchedSiteOrNull = Sites[touchedSiteId];
        }
    }

    static void Init(string[] args)
    {
        Debug("=====================");
        Debug("START AT" + DateTime.Now);

        MapState.SiteCount = int.Parse(Console.ReadLine());
        MapState.Sites = new Site[MapState.SiteCount];

        for (int i = 0; i < MapState.SiteCount; i++)
        {
            ReadBuffer = Console.ReadLine().Split(' ');
            int siteId = int.Parse(ReadBuffer[0]);
            int x = int.Parse(ReadBuffer[1]);
            int y = int.Parse(ReadBuffer[2]);
            int radius = int.Parse(ReadBuffer[3]);

            Debug("  Init site " + siteId);
            MapState.Sites[i] = new Site(siteId, radius, new Point(x, y));
        }
    }

    /// <summary>
    /// App entry point
    /// </summary>
    static void Main(string[] args)
    {
        Init(args);

        while (true)
        {
            MainTick();

            GoldAvailable += 10;
        }
    }
}