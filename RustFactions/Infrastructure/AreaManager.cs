﻿namespace Oxide.Plugins
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using UnityEngine;

  public partial class RustFactions
  {
    class AreaManager : RustFactionsManager
    {
      MapGrid Grid;
      Dictionary<string, Area> Areas;
      Area[,] Layout;

      public int Count
      {
        get { return Areas.Count; }
      }

      public AreaManager(RustFactions core)
        : base(core)
      {
        Grid = new MapGrid(ConVar.Server.worldsize);
        Areas = new Dictionary<string, Area>();
        Layout = new Area[Grid.NumberOfCells, Grid.NumberOfCells];
      }
      
      public Area Get(string areaId)
      {
        Area area;
        if (Areas.TryGetValue(areaId, out area))
          return area;
        else
          return null;
      }

      public Area[] GetAll()
      {
        return Areas.Values.ToArray();
      }

      public Area[] GetAllByType(AreaType type)
      {
        return Areas.Values.Where(a => a.Type == type).ToArray();
      }

      public Area[] GetAllClaimedByFaction(Faction faction)
      {
        return GetAllClaimedByFaction(faction.Id);
      }

      public Area[] GetAllClaimedByFaction(string factionId)
      {
        return Areas.Values.Where(a => a.FactionId == factionId).ToArray();
      }

      public Area GetByClaimCupboard(BuildingPrivlidge cupboard)
      {
        return GetByClaimCupboard(cupboard.net.ID);
      }

      public Area GetByClaimCupboard(uint cupboardId)
      {
        return Areas.Values.FirstOrDefault(a => a.ClaimCupboard != null && a.ClaimCupboard.net.ID == cupboardId);
      }

      public Town GetTown(string name)
      {
        Area[] areas = GetAllByType(AreaType.Town).Where(area => area.Name == name).ToArray();
        if (areas.Length == 0)
          return null;
        else
          return new Town(areas);
      }

      public Town[] GetAllTowns()
      {
        return GetAllByType(AreaType.Town).GroupBy(a => a.Name).Select(group => new Town(group)).ToArray();
      }

      public Town GetTownByMayor(User user)
      {
        return GetAllTowns().FirstOrDefault(town => town.MayorId == user.Id);
      }

      public Area GetByEntityPosition(BaseEntity entity)
      {
        var x = entity.transform.position.x;
        var z = entity.transform.position.z;
        var offset = MapGrid.GridCellSize / 2;

        int row;
        for (row = 0; row < Grid.NumberOfCells; row++)
        {
          Vector3 position = Layout[row, 0].Position;
          if (z >= position.z - offset && z <= position.z + offset)
            break;
        }

        int col;
        for (col = 0; col < Grid.NumberOfCells; col++)
        {
          Vector3 position = Layout[0, col].Position;
          if (x >= position.x - offset && x <= position.x + offset)
            break;
        }

        return Layout[row, col];
      }

      public void Claim(Area area, AreaType type, Faction faction, User claimant, BuildingPrivlidge cupboard)
      {
        string previousFactionId = area.FactionId;

        area.Type = type;
        area.FactionId = faction.Id;
        area.ClaimantId = claimant.Id;
        area.ClaimCupboard = cupboard;
        RecalculateTerritoryDepth(faction);

        if (previousFactionId != null)
        {
          RecalculateTerritoryDepth(previousFactionId);
          SelectNewHeadquartersIfNecessary(previousFactionId);
        }

        Core.OnAreasChanged();
      }

      public void SetHeadquarters(Area area, Faction faction)
      {
        // Ensure that no other areas are considered headquarters.
        foreach (Area otherArea in GetAllClaimedByFaction(faction).Where(a => a.Type == AreaType.Headquarters))
          otherArea.Type = AreaType.Claimed;

        area.Type = AreaType.Headquarters;
        Core.OnAreasChanged();
      }

      public void AddToTown(string name, User mayor, params Area[] areas)
      {
        foreach (Area area in areas)
        {
          area.Type = AreaType.Town;
          area.Name = name;
          area.ClaimantId = mayor.Id;
        }
        Core.OnAreasChanged();
      }

      public void RemoveFromTown(params Area[] areas)
      {
        foreach (Area area in areas)
        {
          area.Type = AreaType.Claimed;
          area.Name = null;
        }
        Core.OnAreasChanged();
      }

      public void Unclaim(IEnumerable<Area> areas)
      {
        Unclaim(areas.ToArray());
      }

      public void Unclaim(params Area[] areas)
      {
        string[] factionIds = areas.Select(a => a.FactionId).Distinct().ToArray();

        foreach (Area area in areas)
        {
          area.Type = AreaType.Wilderness;
          area.FactionId = null;
          area.ClaimantId = null;
          area.ClaimCupboard = null;
        }

        foreach (string factionId in factionIds)
          SelectNewHeadquartersIfNecessary(factionId);

        Core.OnAreasChanged();
      }

      public void AddBadlands(params Area[] areas)
      {
        foreach (Area area in areas)
        {
          area.Type = AreaType.Badlands;
          area.FactionId = null;
          area.ClaimantId = null;
          area.ClaimCupboard = null;
        }
        Core.OnAreasChanged();
      }

      public void AddBadlands(IEnumerable<Area> areas)
      {
        AddBadlands(areas.ToArray());
      }

      public Area SelectNewHeadquartersIfNecessary(Faction faction)
      {
        return SelectNewHeadquartersIfNecessary(faction.Id);
      }

      public Area SelectNewHeadquartersIfNecessary(string factionId)
      {
        Area[] areas = GetAllClaimedByFaction(factionId)
          .Where(a => a.Type == AreaType.Claimed)
          .OrderByDescending(a => a.TerritoryDepth).ToArray();

        if (areas.Length == 0 || areas.Any(a => a.Type == AreaType.Headquarters))
          return null;

        // If the faction no longer has a headquarters, move it to an area at the center of their territory.
        areas[0].Type = AreaType.Headquarters;

        Core.OnAreasChanged();
        return areas[0];
      }

      public void RecalculateTerritoryDepth(Faction faction)
      {
        RecalculateTerritoryDepth(faction.Id);
      }

      public void RecalculateTerritoryDepth(string factionId)
      {
        foreach (Area area in GetAllClaimedByFaction(factionId))
          area.TerritoryDepth = GetDepthInsideFriendlyTerritory(area);
      }

      int GetDepthInsideFriendlyTerritory(Area area)
      {
        if (!area.IsClaimed)
          return 0;

        var depth = new int[4];

        for (var row = area.Row; row >= 0; row--)
        {
          if (Layout[row, area.Col].FactionId != area.FactionId)
            break;

          depth[0]++;
        }

        for (var row = area.Row; row < Grid.NumberOfCells; row++)
        {
          if (Layout[row, area.Col].FactionId != area.FactionId)
            break;

          depth[1]++;
        }

        for (var col = area.Col; col >= 0; col--)
        {
          if (Layout[area.Row, col].FactionId != area.FactionId)
            break;

          depth[2]++;
        }

        for (var col = area.Col; col < Grid.NumberOfCells; col++)
        {
          if (Layout[area.Row, col].FactionId != area.FactionId)
            break;

          depth[3]++;
        }

        return depth.Min() - 1;
      }

      public void Init(AreaInfo[] areaInfos)
      {
        Puts("Creating area objects...");

        Dictionary<string, AreaInfo> lookup;
        if (areaInfos != null)
          lookup = areaInfos.ToDictionary(a => a.AreaId);
        else
          lookup = new Dictionary<string, AreaInfo>();

        for (var row = 0; row < Grid.NumberOfCells; row++)
        {
          for (var col = 0; col < Grid.NumberOfCells; col++)
          {
            string areaId = Grid.GetAreaId(row, col);
            Vector3 position = Grid.GetPosition(row, col);
            Vector3 size = new Vector3(MapGrid.GridCellSize, 500, MapGrid.GridCellSize);

            AreaInfo info = null;
            lookup.TryGetValue(areaId, out info);

            var area = new GameObject().AddComponent<Area>();
            area.Init(Core, areaId, row, col, position, size, info);

            Areas[areaId] = area;
            Layout[row, col] = area;
          }
        }

        Puts($"Created {Areas.Values.Count} area objects.");
      }

      public void Destroy()
      {
        var areaObjects = Resources.FindObjectsOfTypeAll<Area>();
        Puts($"Destroying {areaObjects.Length} area objects...");

        foreach (var area in areaObjects)
        {
          var collider = area.GetComponent<BoxCollider>();
          if (collider != null)
            UnityEngine.Object.Destroy(collider);
          UnityEngine.Object.Destroy(area);
        }

        Areas.Clear();
        Array.Clear(Layout, 0, Layout.Length);

        Puts("Area objects destroyed.");
      }

      public AreaInfo[] SerializeState()
      {
        return Areas.Values.Select(area => area.Serialize()).ToArray();
      }
    }
  }
}
