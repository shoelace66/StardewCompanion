using System.Text.RegularExpressions;

namespace StardewCompanion.Protocol;

public sealed class CapabilityStatus
{
    public string Name {get;set;}="";
    public string Group {get;set;}="";
    public string State {get;set;}="unimplemented";
    public string Reason {get;set;}="";
    public bool Implemented {get;set;}
    public bool Validated {get;set;}
}
public sealed class BusinessCapability
{
    public string Name {get;}
    public string Group {get;}
    public string Description {get;}
    public string PropertiesJson {get;}
    public string[] Required {get;}
    public bool Query {get;}
    public BusinessCapability(string name,string group,string description,string properties,string[] required,bool query=false)
    {Name=name;Group=group;Description=description;PropertiesJson=properties;Required=required;Query=query;}
}
public static class BusinessCapabilities
{
    public static string PublicReferenceName(string key)
    {
        if(key=="npc")return "npcName";
        if(key=="responseKey")return "responseName";
        if(key.EndsWith("Ids",StringComparison.Ordinal))return key.Substring(0,key.Length-3)+"Names";
        if(key.EndsWith("Id",StringComparison.Ordinal))return key.Substring(0,key.Length-2)+"Name";
        return key;
    }
    private static string PublicSchema(string properties)
    {
        var internalNames=Regex.Matches(properties,"\"([A-Za-z0-9]+(?:Ids|Id)|npc|responseKey)\"\\s*:")
            .Cast<Match>().Select(match=>match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        foreach(var internalName in internalNames){
            var publicName=PublicReferenceName(internalName);
            properties=properties.Replace("\""+internalName+"\"","\""+publicName+"\"");
        }
        return properties;
    }
    public static readonly string[] Groups={"farm_management","resources_exploration","production_crafting","storage_economy","navigation_movement","construction_space","social_progress","character_schedule"};
    public static string GroupTitle(string group)=>group switch{
        "farm_management"=>"01 农场经营","resources_exploration"=>"02 资源获取与探险","production_crafting"=>"03 生产与制作","storage_economy"=>"04 仓储与经济",
        "navigation_movement"=>"05 导航与移动","construction_space"=>"06 建筑与空间管理","social_progress"=>"07 社交与世界进度","character_schedule"=>"08 角色维护与日程",_=>group};
    public static string GroupFor(string skill)=>Find(skill)?.Group??skill switch{
        "go_to" or "follow_player" or "return_home" or "travel" or "mine_travel"=>"navigation_movement",
        "water_area" or "harvest_area" or "refill_water" or "cultivate_plot" or "maintain_plot" or "harvest_and_replant" or "plant_area" or "till_area" or "clear_area" or "plant_trees" or "tend_animals" or "manage_livestock" or "survey_area"=>"farm_management",
        "forage_area" or "forage_route" or "gather_resource" or "fish_at" or "fight_enemies"=>"resources_exploration",
        "collect_outputs" or "service_machines" or "craft_items" or "cook_recipes" or "craft_and_place" or "install_irrigation"=>"production_crafting",
        "deposit_items" or "withdraw_items" or "organize_storage" or "prepare_loadout" or "purchase_items" or "sell_items" or "upgrade_tool" or "collect_upgraded_tool"=>"storage_economy",
        "order_building" or "modify_building"=>"construction_space",
        "collect_daily_info" or "visit_villagers" or "manage_quests" or "donate_bundle" or "donate_museum" or "complete_public_project" or "attend_event" or "respond_dialogue"=>"social_progress",
        "recover" or "end_day" or "care_for_pets" or "sleep"=>"character_schedule",_=>"social_progress"};
    private const string Region = @"
        ""location"":{""type"":""string""},""x"":{""type"":""integer"",""minimum"":0},""y"":{""type"":""integer"",""minimum"":0},
        ""width"":{""type"":""integer"",""minimum"":1,""maximum"":32},""height"":{""type"":""integer"",""minimum"":1,""maximum"":32}
        ";
    private const string Water = @",""waterPolicy"":{""type"":""string"",""enum"":[""water_now"",""skip_today""]}";
    private static BusinessCapability Define(string name,string group,string description,string properties,string required,bool query=false)
    {
        var keys=required.Split(new[]{','},StringSplitOptions.RemoveEmptyEntries).ToList();
        if(name is "craft_items" or "cook_recipes" or "craft_and_place" or "install_irrigation" or "order_building" or "modify_building"){
            keys.Remove("sourceChests");
            description+="；省略或空sourceChests时自动从授权工作箱补取缺料，非空时限制来源";
            if(name=="modify_building")properties+=@",""sourceChests"":{""type"":""array"",""items"":{""type"":""string""}}";
        }
        if(name=="visit_villagers"){
            description+="；脚本按姓名实时定位，无需先观察NPC或指定地图";
        }
        if(name is "tend_animals" or "care_for_pets" or "manage_livestock")keys.Remove("location");
        if(name=="manage_quests"){keys.Remove("npc");description+="；送货对象从任务读取并实时定位，领奖/取消无需前往指定地图";}
        if(name=="tend_animals"){keys.Remove("animalIds");description+="；按动物名称和畜舍上下文定位，省略animalNames/location时照料农场全部动物";}
        if(name=="care_for_pets"){keys.Remove("petIds");description+="；按宠物名称定位，省略petNames/location时处理农场宠物";}
        if(name=="manage_livestock")description+="；出售/迁舍按动物名称及畜舍上下文定位；buildingName使用观察中的唯一畜舍名称，孵化仅确认入孵，不宣称动物已孵出";
        if(name=="attend_event")keys.Remove("eventId");
        if(name is "collect_outputs" or "service_machines"){
            keys.Remove("targets");keys.Remove("location");
            properties+=@",""machineName"":{""type"":""string""},""region"":{""type"":""object"",""properties"":{""x"":{""type"":""integer"",""minimum"":0},""y"":{""type"":""integer"",""minimum"":0},""width"":{""type"":""integer"",""minimum"":1,""maximum"":32},""height"":{""type"":""integer"",""minimum"":1,""maximum"":32}},""required"":[""x"",""y"",""width"",""height""],""additionalProperties"":false}";
            if(name=="service_machines")properties+=@",""inputId"":{""type"":[""string"",""null""]},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""fuelId"":{""type"":[""string"",""null""]}";
            description+="；省略targets时到location（默认当前地图）按machineName或region自动查找并排序，亦可不指定名称处理区域内全部机器。装料时指定顶层inputName/quality/fuelName";
        }
        var publicSchema="{"+PublicSchema(properties)+"}";
        if(name=="donate_bundle")publicSchema=publicSchema.Replace("\"bundleName\":{\"type\":\"integer\"}","\"bundleName\":{\"type\":\"string\"}");
        foreach(var pair in new[]{("animalId","animalName"),("animalIds","animalNames"),("buildingId","buildingName"),("itemId","itemName"),("seedId","seedName"),("toolId","toolName"),("shopId","shopName"),("eventId","eventName"),("questId","questName"),("projectId","projectName"),("bundleId","bundleName")})description=description.Replace(pair.Item1,pair.Item2);
        var publicRequired=keys.Select(PublicReferenceName).Distinct(StringComparer.Ordinal).ToArray();
        return new(name,group,description,publicSchema,publicRequired,query);
    }
    public static IReadOnlyList<BusinessCapability> All {get;}=new[]{
        Define("claim_starter_gift","farm_management","自动回到自己的农舍、打开原版初始礼包并核验实际入包物品；初始15包防风草种子属于免费礼包，无需工作箱授权。已领取不会重复生成物品","",""),
        Define("survey_area","farm_management","读取当前区域的障碍、可耕格、作物、洒水器实际覆盖、可接近位置和成本估算，不修改世界",Region,"location,x,y,width,height",true),
        Define("cultivate_plot","farm_management","一项工作完成清障、锄地、指定施肥、普通作物播种和可选浇水；不覆盖活作物、不扩大范围",Region+@"
            ,""seedId"":{""type"":""string""},""plantCount"":{""type"":""integer"",""minimum"":1,""maximum"":1024},""fertilizerId"":{""type"":[""string"",""null""]},
            ""clearPolicy"":{""type"":""string"",""enum"":[""none"",""debris"",""debris_and_wild_trees""]},""allowPartial"":{""type"":""boolean""}
            "+Water,"location,x,y,width,height,seedId,plantCount,fertilizerId,clearPolicy,allowPartial,waterPolicy"),
        Define("maintain_plot","farm_management","按明确选项维护区域：浇水、收获、清枯作物和补种；不移除仍活作物",Region+@"
            ,""harvest"":{""type"":""boolean""},""removeDead"":{""type"":""boolean""},""replantSeedId"":{""type"":[""string"",""null""]},""fertilizerId"":{""type"":[""string"",""null""]}
            "+Water,"location,x,y,width,height,harvest,removeDead,replantSeedId,fertilizerId,waterPolicy"),
        Define("harvest_and_replant","farm_management","收获成熟作物、拾取、对空耕地用指定种子补种施肥并按策略浇水",Region+@"
            ,""seedId"":{""type"":""string""},""plantCount"":{""type"":""integer"",""minimum"":1,""maximum"":1024},""fertilizerId"":{""type"":[""string"",""null""]},""allowPartial"":{""type"":""boolean""}
            "+Water,"location,x,y,width,height,seedId,plantCount,fertilizerId,allowPartial,waterPolicy"),
        Define("gather_resource","resources_exploration","在指定区域按指定来源连续获取资源，以本次真实入包增量计数；不处理无关目标",Region+@"
            ,""itemId"":{""type"":""string"",""enum"":[""(O)388"",""(O)390"",""(O)771"",""(O)178""]},""count"":{""type"":""integer"",""minimum"":1,""maximum"":9999},
            ""sources"":{""type"":""array"",""minItems"":1,""items"":{""type"":""string"",""enum"":[""twigs"",""wild_trees"",""small_stumps"",""stones"",""weeds"",""grass""]}},""removeStumps"":{""type"":""boolean""}
            ","location,x,y,width,height,itemId,count,sources,removeStumps"),
        Define("plant_trees","farm_management","按明确坐标原生种野树种子或果树苗，核验空地、间距和当前季节；不替换已有树木",
            @"""location"":{""type"":""string""},""itemId"":{""type"":""string""},""tiles"":{""type"":""array"",""minItems"":1,""maxItems"":64,""items"":{""type"":""object"",""properties"":{""x"":{""type"":""integer""},""y"":{""type"":""integer""}},""required"":[""x"",""y""],""additionalProperties"":false}}","location,itemId,tiles"),
        Define("forage_route","resources_exploration","沿明确路线处理可见地面采集、指定灌木树果或挖掘点，不编造远处产物",
            @"""stops"":{""type"":""array"",""minItems"":1,""maxItems"":16,""items"":{""type"":""object"",""properties"":{"+Region+@"
            ,""shake"":{""type"":""boolean""},""digSpots"":{""type"":""boolean""}},""required"":[""location"",""x"",""y"",""width"",""height"",""shake"",""digSpots""],""additionalProperties"":false}}
            ","stops"),
        Define("collect_outputs","production_crafting","收取指定设施已完成产物，不加料、不破坏未就绪机器",
            @"""location"":{""type"":""string""},""targets"":{""type"":""array"",""minItems"":1,""maxItems"":128,""items"":{""type"":""object"",""properties"":{""x"":{""type"":""integer""},""y"":{""type"":""integer""}},""required"":[""x"",""y""],""additionalProperties"":false}}","location,targets"),
        Define("service_machines","production_crafting","逐台收产和补指定原料；支持只收、只补或收后补，核验原版加工状态",
            @"""location"":{""type"":""string""},""mode"":{""type"":""string"",""enum"":[""collect"",""load"",""collect_and_load""]},""targets"":{""type"":""array"",""minItems"":1,""maxItems"":128,""items"":{""type"":""object"",""properties"":{""x"":{""type"":""integer""},""y"":{""type"":""integer""},""inputId"":{""type"":[""string"",""null""]},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""fuelId"":{""type"":[""string"",""null""]}},""required"":[""x"",""y"",""inputId"",""quality"",""fuelId""],""additionalProperties"":false}}","location,mode,targets"),
        Define("craft_items","production_crafting","按已学配方制作，自动从玩家已授权工作箱取齐缺少的材料；省略或留空sourceChests时自动选择，非空时仅使用指定箱；不自动采集或购买缺料",
            @"""recipe"":{""type"":""string""},""count"":{""type"":""integer"",""minimum"":1,""maximum"":999},""sourceChests"":{""type"":""array"",""description"":""可选；省略或空数组时从已授权箱自动取料，非空时限制来源"",""items"":{""type"":""string""}}","recipe,count"),
        Define("cook_recipes","production_crafting","使用已学烹饪配方和明确允许的食材品质，在可用厨房批量烹饪，与普通物品制作分开",
            @"""recipe"":{""type"":""string""},""count"":{""type"":""integer"",""minimum"":1,""maximum"":999},""sourceChests"":{""type"":""array"",""items"":{""type"":""string""}},""ingredientQuality"":{""type"":""integer"",""enum"":[0,1,2,4]}","recipe,count,sourceChests,ingredientQuality"),
        Define("craft_and_place","production_crafting","明确取料、制作和位置后放置，缺料不自行外出；不覆盖已有设施",
            @"""recipe"":{""type"":""string""},""itemId"":{""type"":""string""},""sourceChests"":{""type"":""array"",""items"":{""type"":""string""}},""location"":{""type"":""string""},""tiles"":{""type"":""array"",""minItems"":1,""maxItems"":64,""items"":{""type"":""object"",""properties"":{""x"":{""type"":""integer""},""y"":{""type"":""integer""}},""required"":[""x"",""y""],""additionalProperties"":false}}","recipe,itemId,sourceChests,location,tiles"),
        Define("install_irrigation","farm_management","取用或制作明确的洒水器，按指定位置和附件放置，核验明日覆盖；当天是否浇水独立选择",
            @"""location"":{""type"":""string""},""itemId"":{""type"":""string""},""recipe"":{""type"":[""string"",""null""]},""sourceChests"":{""type"":""array"",""items"":{""type"":""string""}},""placements"":{""type"":""array"",""minItems"":1,""maxItems"":64,""items"":{""type"":""object"",""properties"":{""x"":{""type"":""integer""},""y"":{""type"":""integer""},""attachmentId"":{""type"":[""string"",""null""]}},""required"":[""x"",""y"",""attachmentId""],""additionalProperties"":false}}"+Water,"location,itemId,recipe,sourceChests,placements,waterPolicy"),
        Define("organize_storage","storage_economy","来源/目标使用inventory或授权目录中的箱名称/绑定ID；整批先核验授权，再逐个寻路开箱，按规则保留数量品质并报告实际交付及缺口；不转移保护物品",
            @"""rules"":{""type"":""array"",""minItems"":1,""maxItems"":64,""items"":{""type"":""object"",""properties"":{""source"":{""type"":""string""},""destination"":{""type"":""string""},""itemId"":{""type"":""string""},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""count"":{""type"":""integer"",""minimum"":1},""retain"":{""type"":""integer"",""minimum"":0}},""required"":[""source"",""destination"",""itemId"",""quality"",""count"",""retain""],""additionalProperties"":false}}","rules"),
        Define("prepare_loadout","storage_economy","箱子使用授权目录中的名称或绑定ID；整批先核验授权，再寻路开箱，批量取出工作物品、归还明确的多余物，按品质数量核验",
            @"""withdraw"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""chest"":{""type"":""string""},""itemId"":{""type"":""string""},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""count"":{""type"":""integer"",""minimum"":1}},""required"":[""chest"",""itemId"",""quality"",""count""],""additionalProperties"":false}},""deposit"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""chest"":{""type"":""string""},""itemId"":{""type"":""string""},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""count"":{""type"":""integer"",""minimum"":1}},""required"":[""chest"",""itemId"",""quality"",""count""],""additionalProperties"":false}}","withdraw,deposit"),
        Define("recover","character_schedule","模型明确选择食物、温泉、联机床或睡觉；本地不按体力时刻接管",
            @"""method"":{""type"":""string"",""enum"":[""food"",""spa"",""multiplayer_bed"",""sleep""]},""itemId"":{""type"":[""string"",""null""]},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""maxItems"":{""type"":""integer"",""minimum"":0,""maximum"":999},""targetStamina"":{""type"":""number""},""targetHealth"":{""type"":""integer"",""minimum"":0},""maxGameMinutes"":{""type"":""integer"",""minimum"":1,""maximum"":1200}","method,itemId,quality,maxItems,targetStamina,targetHealth,maxGameMinutes"),
        Define("end_day","character_schedule","只执行明确指定的日终步骤和正常上床；核验原版换日，不默认整理或出货",
            @"""storageRules"":{""type"":""array"",""maxItems"":64,""items"":{""type"":""object"",""properties"":{""source"":{""type"":""string""},""destination"":{""type"":""string""},""itemId"":{""type"":""string""},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""count"":{""type"":""integer"",""minimum"":1},""retain"":{""type"":""integer"",""minimum"":0}},""required"":[""source"",""destination"",""itemId"",""quality"",""count"",""retain""],""additionalProperties"":false}},
            ""shippingItems"":{""type"":""array"",""maxItems"":64,""items"":{""type"":""object"",""properties"":{""itemId"":{""type"":""string""},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""count"":{""type"":""integer"",""minimum"":1},""retain"":{""type"":""integer"",""minimum"":0}},""required"":[""itemId"",""quality"",""count"",""retain""],""additionalProperties"":false}},""sleep"":{""type"":""boolean""}","storageRules,shippingItems,sleep"),
        Define("travel","navigation_movement","原版出行：步行、已解锁交通或明确消耗的传送物；不任意传送",
            @"""method"":{""type"":""string"",""enum"":[""walk"",""bus"",""boat"",""minecart"",""totem"",""return_scepter"",""horse""]},""location"":{""type"":""string""},""x"":{""type"":""integer""},""y"":{""type"":""integer""},""itemId"":{""type"":[""string"",""null""]},""maxCost"":{""type"":""integer"",""minimum"":0}","method,location,x,y,itemId,maxCost"),
        Define("tend_animals","farm_management","照料明确畜舍或动物：喂食、抚摸、挤奶剪毛和收产，可跳过已满足状态",
            @"""location"":{""type"":""string""},""animalIds"":{""type"":""array"",""items"":{""type"":""string""}},""feed"":{""type"":""boolean""},""pet"":{""type"":""boolean""},""milk"":{""type"":""boolean""},""shear"":{""type"":""boolean""},""collectGround"":{""type"":""boolean""},""collectGrabbers"":{""type"":""boolean""},""door"":{""type"":""string"",""enum"":[""unchanged"",""open"",""closed""]}","location,animalIds,feed,pet,milk,shear,collectGround,collectGrabbers,door"),
        Define("care_for_pets","character_schedule","按明确选项抚摸宠物、给碗加水和收礼物，不与畜牧混用",
            @"""location"":{""type"":""string""},""petIds"":{""type"":""array"",""items"":{""type"":""string""}},""pet"":{""type"":""boolean""},""waterBowls"":{""type"":""boolean""},""collectGifts"":{""type"":""boolean""}","location,petIds,pet,waterBowls,collectGifts"),
        Define("manage_livestock","farm_management","明确购买、命名、迁舍、孵化、门状态或出售；purchase按真实菜单animalType名称和maxCost预算购买；通过观察中的动物名称和畜舍名称选择目标，不自动替换目标",
            @"""operation"":{""type"":""string"",""enum"":[""purchase"",""rehome"",""incubate"",""door"",""sell""]},""location"":{""type"":""string""},""animalId"":{""type"":[""string"",""null""]},""animalType"":{""type"":[""string"",""null""]},""name"":{""type"":[""string"",""null""]},""buildingId"":{""type"":""string""},""itemId"":{""type"":[""string"",""null""]},""maxCost"":{""type"":""integer"",""minimum"":0},""door"":{""type"":""string"",""enum"":[""unchanged"",""open"",""closed""]}","operation,location,animalId,animalType,name,buildingId,itemId,maxCost,door"),
        Define("purchase_items","storage_economy","所有商店按itemName准确名称、数量、单价上限和总预算购买，不替换商品；工具升级、建筑和动物用对应技能。自动寻路开店，已知开门前30游戏分钟内等候；失败返回原因、恢复条件和真实目录。location填商店地图，shopName填观察中商店名称。",
            @"""location"":{""type"":""string""},""shopId"":{""type"":""string""},""maxTotal"":{""type"":""integer"",""minimum"":0},""items"":{""type"":""array"",""minItems"":1,""maxItems"":64,""items"":{""type"":""object"",""properties"":{""itemName"":{""type"":""string""},""quality"":{""type"":""integer"",""enum"":[0,1,2,4],""description"":""可选：目录同名商品按明确品质消歧""},""count"":{""type"":""integer"",""minimum"":1},""maxUnitPrice"":{""type"":""integer"",""minimum"":0}},""required"":[""itemName"",""count"",""maxUnitPrice""],""additionalProperties"":false}}","location,shopId,maxTotal,items"),
        Define("sell_items","storage_economy","按背包的原版物品名称和品质出售明确数量。同物品同品质的多行合并数量，采用最高保留数；商店销售核对到账，出货箱等夜间结算",
            @"""channel"":{""type"":""string"",""enum"":[""shipping"",""shop""]},""location"":{""type"":""string""},""shopId"":{""type"":[""string"",""null""]},""items"":{""type"":""array"",""minItems"":1,""maxItems"":64,""items"":{""type"":""object"",""properties"":{""itemId"":{""type"":""string""},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""count"":{""type"":""integer"",""minimum"":1},""retain"":{""type"":""integer"",""minimum"":0}},""required"":[""itemId"",""quality"",""count"",""retain""],""additionalProperties"":false}}","channel,location,shopId,items"),
        Define("upgrade_tool","storage_economy","按明确等级和费用上限送工具升级，toolId使用观察到的工具ID；upgradeLevel等级映射：1=铜、2=铁、3=金、4=铱；需要5个对应金属锭。记录已下单与不可用期，不当作已升级",
            @"""toolId"":{""type"":""string"",""description"":""背包中待升级工具的原版ID，例如(T)Hoe""},""upgradeLevel"":{""type"":""integer"",""minimum"":1,""maximum"":4,""description"":""目标等级：1=铜、2=铁、3=金、4=铱；此处不是目标工具的最终UpgradeLevel""},""maxCost"":{""type"":""integer"",""minimum"":0,""description"":""可接受的最高金币费用""}","toolId,upgradeLevel,maxCost"),
        Define("collect_upgraded_tool","storage_economy","检查并原生取回已完成升级的工具；未完成时返回等待条件",
            @"""toolId"":{""type"":""string""}","toolId"),
        Define("order_building","construction_space","按明确位置、材料和预算原生下单建筑，不视为建设完成",
            @"""buildingType"":{""type"":""string""},""x"":{""type"":""integer""},""y"":{""type"":""integer""},""maxCost"":{""type"":""integer"",""minimum"":0},""sourceChests"":{""type"":""array"",""items"":{""type"":""string""}}","buildingType,x,y,maxCost,sourceChests"),
        Define("modify_building","construction_space","按指定建筑升级、移动或拆除；不自行变更建筑",
            @"""operation"":{""type"":""string"",""enum"":[""upgrade"",""move"",""demolish""]},""buildingId"":{""type"":""string""},""upgradeType"":{""type"":[""string"",""null""]},""x"":{""type"":""integer""},""y"":{""type"":""integer""},""maxCost"":{""type"":""integer"",""minimum"":0}","operation,buildingId,upgradeType,x,y,maxCost"),
        Define("collect_daily_info","social_progress","脚本自动前往邮箱、电视、公告板等真实来源读取指定信息；只需sources，不获取未观察地图实时内容",
            @"""sources"":{""type"":""array"",""minItems"":1,""items"":{""type"":""string"",""enum"":[""mail"",""weather"",""fortune"",""recipes"",""calendar"",""help_wanted"",""special_orders"",""quests""]}}","sources"),
        Define("visit_villagers","social_progress","按名单交谈或交付明确礼物，遵守原版限制，不自行决定礼物",
            @"""visits"":{""type"":""array"",""minItems"":1,""maxItems"":16,""items"":{""type"":""object"",""properties"":{""npc"":{""type"":""string""},""talk"":{""type"":""boolean""},""giftId"":{""type"":[""string"",""null""]},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""dialogueResponses"":{""type"":""array"",""items"":{""type"":""string""}}},""required"":[""npc"",""talk"",""giftId"",""quality"",""dialogueResponses""],""additionalProperties"":false}}","visits"),
        Define("manage_quests","social_progress","按明确任务接取、交付、领取奖励或取消；不把背包已有材料当新收集进度",
            @"""operation"":{""type"":""string"",""enum"":[""accept"",""deliver"",""claim"",""cancel""]},""questId"":{""type"":""string""},""npc"":{""type"":[""string"",""null""]},""items"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""itemId"":{""type"":""string""},""quality"":{""type"":""integer""},""count"":{""type"":""integer"",""minimum"":1}},""required"":[""itemId"",""quality"",""count""],""additionalProperties"":false}}","operation,questId,npc,items"),
        Define("donate_bundle","social_progress","社区中心献祭专用：只用 area,bundleId,slot,itemId,quality,count,claimReward；不使用 donate_museum 的 items/x/y/claimRewards 字段。area、bundleId和未完成slot须来自当前BundleData/卷轴观察",
            @"""area"":{""type"":""integer""},""bundleId"":{""type"":""integer""},""slot"":{""type"":""integer""},""itemId"":{""type"":""string""},""quality"":{""type"":""integer"",""enum"":[0,1,2,4]},""count"":{""type"":""integer"",""minimum"":1},""claimReward"":{""type"":""boolean""}","area,bundleId,slot,itemId,quality,count,claimReward"),
        Define("donate_museum","social_progress","博物馆捐赠专用：只用 items:[{itemId,x,y}] 和 claimRewards；不要传 donate_bundle 的 area,bundleId,slot,quality,count,claimReward 字段。坐标须来自当前有效空展位观察",
            @"""items"":{""type"":""array"",""minItems"":1,""maxItems"":64,""items"":{""type"":""object"",""properties"":{""itemId"":{""type"":""string""},""x"":{""type"":""integer""},""y"":{""type"":""integer""}},""required"":[""itemId"",""x"",""y""],""additionalProperties"":false}},""claimRewards"":{""type"":""boolean""}","items,claimRewards"),
        Define("complete_public_project","social_progress","按项目明确支付材料或金额，核验提交与实际解锁的区别；不替模型选择发展路线",
            @"""projectId"":{""type"":""string""},""location"":{""type"":""string""},""maxCost"":{""type"":""integer"",""minimum"":0},""items"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""itemId"":{""type"":""string""},""count"":{""type"":""integer"",""minimum"":1}},""required"":[""itemId"",""count""],""additionalProperties"":false}},""dialogueResponses"":{""type"":""array"",""items"":{""type"":""string""}}","projectId,location,maxCost,items,dialogueResponses"),
        Define("attend_event","social_progress","参加当前可接触且已适配的活动；dialogue可指定npcName，省略时与最近的可见活动NPC交谈并只按原版对话选项回应；购买按真实Shop动作开店，离场确认后核验事件结束；不支持的活动返回具体原因",
            @"""eventId"":{""type"":""string""},""location"":{""type"":""string""},""activity"":{""type"":""string"",""enum"":[""enter"",""dialogue"",""shop"",""minigame"",""leave""]},""npcName"":{""type"":""string"",""description"":""可选：当前活动内可见NPC名称""},""dialogueResponses"":{""type"":""array"",""items"":{""type"":""string""}},""itemName"":{""type"":""string"",""description"":""shop活动必填：真实商品名称""},""shopId"":{""type"":""string"",""description"":""尚未打开菜单时指定当前活动可见商店代码""},""count"":{""type"":""integer"",""minimum"":0},""maxCost"":{""type"":""integer"",""minimum"":0}","eventId,location,activity,dialogueResponses,count,maxCost")
    };
    public static BusinessCapability? Find(string name)=>All.FirstOrDefault(c=>c.Name==name);
}
