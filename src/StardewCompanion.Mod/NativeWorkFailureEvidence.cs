using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewCompanion.Mod;

internal sealed partial class NativeWorkExecutor
{
    private bool _failureRecorded;
    private static readonly JsonSerializerOptions FailureEvidenceJson=new(){DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull};
    private void RecordFailureEvidence(string reason)
    {
        if(_failureRecorded)return;_failureRecorded=true;
        var needsInventory=reason.Contains("item")||reason.Contains("seed")||reason.Contains("material")||reason.Contains("ingredient")||reason.Contains("fuel")||reason.Contains("gift")||reason.Contains("tool");
        var needsRecipes=reason.Contains("recipe");
        var needsNpc=reason.Contains("npc")||reason.Contains("villager")||reason.Contains("gift");
        var needsBuildings=reason.Contains("building")||reason.Contains("home");
        var needsAnimals=reason.Contains("animal")||reason.Contains("livestock")||reason.Contains("pet_");
        _results.Add(JsonSerializer.SerializeToElement(new{
            failure=reason,phase=Phase,location=Game1.currentLocation.NameOrUniqueName,gameDay=(int)Game1.Date.TotalDays,timeOfDayHHMM=Game1.timeOfDay,
            verifiedCompleted=Completed,
            lastNativePrompt=GameEventTracker.LastDialogueCapture is {} capture&&capture.Revision>_promptRevisionBefore?capture.Text:null,
            shop=Game1.activeClickableMenu is ShopMenu openShop?new{shopId=openShop.ShopId,items=ShopCatalog(openShop)}:null,
            activeEvent=Game1.currentLocation.currentEvent is {} currentEvent?new{id=currentEvent.id,isFestival=currentEvent.isFestival}:null,
            buildingOffers=Game1.activeClickableMenu is CarpenterMenu carpenter?carpenter.Blueprints.Select(b=>new{
                buildingType=b.Id,name=Game1.buildingData.TryGetValue(b.Id,out var d)?StardewValley.TokenizableStrings.TokenParser.ParseText(d.Name):b.Id,cost=b.BuildCost}).ToArray():null,
            animalOffers=Game1.activeClickableMenu is PurchaseAnimalsMenu animalMenu?animalMenu.animalsToPurchase.Select(b=>new{
                animalType=b.hoverText,name=AnimalOfferName(b),price=b.name,unlocked=b.item is StardewValley.Object {Type:not null}}).ToArray():null,
            inventory=needsInventory?Game1.player.Items.Where(i=>i!=null).Select(i=>new{itemId=i.QualifiedItemId,itemName=i.DisplayName,quality=i.Quality,count=i.Stack,protectedItem=i.modData.ContainsKey("StardewCompanion/Protected")}).ToArray():null,
            tools=reason.Contains("tool")?Game1.player.Items.OfType<Tool>().Select(t=>new{toolId=t.QualifiedItemId,toolName=t.DisplayName,level=t.UpgradeLevel}).ToArray():null,
            pendingTool=reason.Contains("tool")&&Game1.player.toolBeingUpgraded.Value is {} pending?new{toolId=pending.QualifiedItemId,toolName=pending.DisplayName,daysRemaining=Game1.player.daysLeftForToolUpgrade.Value}:null,
            recipes=needsRecipes?(Flag("cooking")?Game1.player.cookingRecipes:Game1.player.craftingRecipes).Keys.ToArray():null,
            npcs=needsNpc?Game1.currentLocation.characters.Select(n=>new{npc=n.Name,name=n.displayName,x=n.TilePoint.X,y=n.TilePoint.Y}).ToArray():null,
            buildings=needsBuildings?Game1.getFarm().buildings.Select(b=>new{buildingId=b.id.Value.ToString(),type=b.buildingType.Value,indoors=b.GetIndoors()?.NameOrUniqueName,constructionDaysRemaining=b.daysOfConstructionLeft.Value,upgradeDaysRemaining=b.daysUntilUpgrade.Value}).ToArray():null,
            animals=needsAnimals?Game1.currentLocation.animals.Values.Select(a=>new{animalId=a.myID.Value.ToString(),name=a.Name,type=a.type.Value,homeId=a.home?.id.Value.ToString()}).ToArray():null,
            dialogueChoices=Game1.activeClickableMenu is DialogueBox dialogue?dialogue.responses.Select(r=>new{key=r.responseKey,text=r.responseText}).ToArray():null,
            quests=reason.Contains("quest")?Game1.player.questLog.Select(q=>new{questId=QuestIdentity.For(q),title=q.questTitle,completed=q.completed.Value}).ToArray():null,
            evidenceMeaning="current observed state only; failure is not completion; use exact identities and recheck prerequisites"
        },FailureEvidenceJson));
    }
}
