using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using StardewCompanion.Protocol;

namespace StardewCompanion.Desktop;

internal sealed class SpringTrialEvidence(string root,string savesRoot,string runId)
{
    private string? _lastObservation;
    private static readonly JsonSerializerOptions Indented=new(){WriteIndented=true};

    internal static void Write(string folder,string name,object data)
    {
        var path=Path.Combine(folder,name);
        var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(data,Indented));
        File.Move(temp,path,true);
    }

    internal void CaptureObservation(GameObservation observation)
    {
        using var document=JsonDocument.Parse(observation.WorldView);
        var world=document.RootElement;
        var key=$"{observation.GameDay}:{observation.TimeOfDay}:{observation.Location}:{observation.Menu}:{observation.ActiveTaskId}:{observation.Paused}:{observation.Stamina}:{world.GetProperty("money")}";
        if(key==_lastObservation)return;
        File.AppendAllText(Path.Combine(root,"observation-snapshots.jsonl"),
            JsonSerializer.Serialize(new{at=DateTimeOffset.UtcNow,observation,world})+Environment.NewLine);
        _lastObservation=key;
    }

    internal bool CaptureCheckpoint(GameObservation observation)
    {
        var season=observation.GameDay==28?"summer":"spring";
        var day=observation.GameDay%28+1;
        var target=Path.Combine(root,"checkpoints",$"{season}-{day:00}");
        if(Directory.Exists(target))return true;
        if(observation.Menu!=""||observation.TestRunId!=runId)return false;
        var worldPath=Path.Combine(root,"world.json");
        var staging=target+".staging";
        try{
            using var world=JsonDocument.Parse(File.ReadAllBytes(worldPath));
            var identity=world.RootElement;
            if(identity.GetProperty("runId").GetString()!=runId||identity.GetProperty("saveId").GetString()!=observation.SaveId)return false;
            var folder=Path.GetFullPath(identity.GetProperty("folder").GetString()??"");
            if(!string.Equals(Path.GetDirectoryName(folder),savesRoot,StringComparison.OrdinalIgnoreCase))return false;
            var filename=Path.GetFileName(folder);
            var native=Path.Combine(folder,filename);
            var info=Path.Combine(folder,"SaveGameInfo");
            var saved=File.ReadAllBytes(native);
            var savedInfo=File.ReadAllBytes(info);
            var save=XDocument.Load(new MemoryStream(saved)).Root!;
            if((string?)save.Element("uniqueIDForThisGame")!=observation.SaveId||(string?)save.Element("currentSeason")!=season||
                (int?)save.Element("dayOfMonth")!=day||(int?)save.Element("year")!=1||
                (string?)save.Element("player")?.Element("farmName")!=runId)return false;
            var metadata=XDocument.Load(new MemoryStream(savedInfo)).Root;
            if((string?)metadata?.Element("farmName")!=runId)return false;
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging,filename),saved);
            File.WriteAllBytes(Path.Combine(staging,"SaveGameInfo"),savedInfo);
            Write(staging,"observation.json",observation);
            if(observation.GameDay==28)WriteReport(filename,save);
            Directory.Move(staging,target);
            return true;
        }
        catch(Exception ex)when(ex is IOException or XmlException or JsonException){return false;}
    }

    private static FarmSnapshot ReadFarm(XElement save)
    {
        var player=save.Element("player")!;
        var farm=save.Element("locations")!.Elements().First(location=>(string?)location.Element("name")=="Farm");
        var friendship=player.Element("friendshipData")!.Elements("item").ToDictionary(
            item=>(string)item.Element("key")!.Element("string")!,
            item=>(int)item.Element("value")!.Element("Friendship")!.Element("Points")!);
        var inventory=player.Element("items")!.Elements("Item").Where(item=>item.Element("name")!=null).ToArray();
        XNamespace instance="http://www.w3.org/2001/XMLSchema-instance";
        var tools=inventory.Where(item=>item.Element("upgradeLevel")!=null)
            .GroupBy(item=>(string?)item.Attribute(instance+"type")??(string)item.Element("name")!)
            .ToDictionary(group=>group.Key,group=>group.Max(item=>(int)item.Element("upgradeLevel")!));
        var buildings=farm.Element("buildings")?.Elements().Select(building=>new{
            id=(string?)building.Element("id"),type=(string?)building.Element("buildingType"),
            constructionDays=(int?)building.Element("daysOfConstructionLeft")??0,
            upgradeDays=(int?)building.Element("daysUntilUpgrade")??0
        }).ToArray();
        var soil=farm.Element("terrainFeatures")?.Descendants("TerrainFeature").Count(tile=>tile.Attributes().Any(a=>a.Name.LocalName=="type"&&a.Value=="HoeDirt"))??0;
        var farmData=new{buildings,soil,hay=(int?)farm.Element("piecesOfHay"),
            animals=farm.Descendants("FarmAnimal").Select(animal=>new{
                id=(string?)animal.Element("myID"),type=(string?)animal.Element("type"),name=(string?)animal.Element("name")
            }).ToArray(), inventory=inventory.Select(item=>new{
                id=(string?)item.Element("itemId"),name=(string?)item.Element("name"),
                quality=(int?)item.Element("quality")??0,count=(int?)item.Element("stack")??1
            }).ToArray()};
        return new FarmSnapshot((int)player.Element("money")!,friendship,tools,farmData,(string?)player.Element("toolBeingUpgraded")?.Element("name"),
            (int?)player.Element("daysLeftForToolUpgrade")??0);
    }

    private sealed record FarmSnapshot(int Cash,Dictionary<string,int> Friendship,Dictionary<string,int> Tools,object Farm,string? PendingTool,int DaysToTool);

    private void WriteReport(string filename,XElement final)
    {
        var initial=XDocument.Load(Path.Combine(root,"checkpoints","spring-01",filename)).Root!;
        var start=ReadFarm(initial);
        var end=ReadFarm(final);
        var friendship=start.Friendship.Keys.Union(end.Friendship.Keys).Order()
            .Select(id=>new{id,initial=start.Friendship.GetValueOrDefault(id),final=end.Friendship.GetValueOrDefault(id),
                delta=end.Friendship.GetValueOrDefault(id)-start.Friendship.GetValueOrDefault(id)}).ToArray();
        Write(root,"summer-01-results.json",new{
            saveId=(string?)final.Element("uniqueIDForThisGame"),
            cash=new{initial=start.Cash,final=end.Cash,delta=end.Cash-start.Cash},
            friendship=new{positive=friendship.Count(row=>row.delta>0),net=friendship.Sum(row=>row.delta),rows=friendship,
                note="涵盖两份完整原版存档中有好感记录的 NPC；另一端无记录时以 0 计。"},
            farm=end.Farm,
            tools=new{initial=start.Tools,retrievedInventory=end.Tools,pending=end.PendingTool,daysRemaining=end.DaysToTool,
                note="工具等级来自玩家背包。箱内工具与取回日期需结合原版存档和执行回执核验。"}
        });
    }
}
