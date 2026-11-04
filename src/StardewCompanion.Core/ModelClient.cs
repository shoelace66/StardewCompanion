using System.Diagnostics;
using StardewCompanion.Protocol;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace StardewCompanion.Core;

public sealed record ModelReply(string Text, JsonObject Message, JsonArray Calls, long ElapsedMs, string Usage);
public sealed class ModelClient(HttpClient http)
{
    public async Task<ModelReply> Complete(ModelSettings settings, JsonArray messages, CancellationToken cancellation, JsonNode? toolChoice = null, bool soloTest=false,CapabilityStatus[]? capabilities=null,JsonArray? toolOverrides=null,Action<JsonObject>? capture=null,int maxTokens=2400)
    {
        var availableTools=toolOverrides??ToolCatalog.Definitions(soloTest,capabilities);
        var body=new JsonObject { ["model"]=settings.Model,["messages"]=messages.DeepClone(),["stream"]=false,["max_tokens"]=maxTokens };
        if(availableTools.Count>0){body["tools"]=availableTools.DeepClone();body["tool_choice"]=toolChoice?.DeepClone()??JsonValue.Create("auto");}
        var payload=body.ToJsonString(DecisionContextCompression.ReadableJson);
        var timer=Stopwatch.StartNew();
        for(var attempt=0;;attempt++){
        try{
        capture?.Invoke(body);
        using var request=new HttpRequestMessage(HttpMethod.Post,settings.ValidateEndpoint());
        request.Version=System.Net.HttpVersion.Version11;request.VersionPolicy=HttpVersionPolicy.RequestVersionExact;
        request.Headers.ConnectionClose=true;request.Headers.ExpectContinue=false;
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",settings.GetKey());
        request.Content=new StringContent(payload,System.Text.Encoding.UTF8,"application/json");
        using var attemptTimeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);attemptTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,attemptTimeout.Token);
        if(!response.IsSuccessStatusCode)throw new HttpRequestException($"模型请求失败（HTTP {(int)response.StatusCode}）",null,response.StatusCode);
        var root=JsonNode.Parse(await response.Content.ReadAsStringAsync(attemptTimeout.Token))!;
        var message=root["choices"]?[0]?["message"]?.AsObject()??throw new InvalidDataException("模型响应没有 message");
        var calls=message["tool_calls"] as JsonArray??new JsonArray();
        return new(message["content"]?.GetValue<string>()??"",message,calls,timer.ElapsedMilliseconds,root["usage"]?.ToJsonString()??"unavailable");
        }
        catch(TaskCanceledException ex)when(!cancellation.IsCancellationRequested){
            if(OperatingSystem.IsWindows())return await CompleteViaWindowsPowerShell(settings,payload,timer,cancellation);
            if(attempt<2){await Task.Delay(TimeSpan.FromSeconds(attempt+1),cancellation);continue;}
            throw new TimeoutException("模型请求连续3次在8秒内未完成",ex);
        }
        catch(IOException)when(!cancellation.IsCancellationRequested&&OperatingSystem.IsWindows()){
            return await CompleteViaWindowsPowerShell(settings,payload,timer,cancellation);
        }
        catch(HttpRequestException ex)when(!cancellation.IsCancellationRequested&&OperatingSystem.IsWindows()&&ex.StatusCode==null){
            return await CompleteViaWindowsPowerShell(settings,payload,timer,cancellation);
        }
        catch(HttpRequestException ex)when(attempt<2&&(ex.StatusCode==null||(int)ex.StatusCode==429||(int)ex.StatusCode>=500)){
            await Task.Delay(TimeSpan.FromSeconds(attempt+1),cancellation);
        }
        }
    }

    private static async Task<ModelReply> CompleteViaWindowsPowerShell(ModelSettings settings,string payload,Stopwatch timer,CancellationToken cancellation)
    {
        const string script="""
        $ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';[Console]::InputEncoding=[Text.Encoding]::UTF8;[Console]::OutputEncoding=[Text.Encoding]::UTF8;
        try{$r=[Console]::In.ReadToEnd()|ConvertFrom-Json;$json=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($r.payload));$headers=@{Authorization=('Bearer '+$r.key)};
        $response=Invoke-WebRequest -UseBasicParsing -Uri $r.endpoint -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 45;
        $decoded=[Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray())|ConvertFrom-Json;
        $out=$decoded|ConvertTo-Json -Depth 100 -Compress;[Console]::Out.Write('OK:'+([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($out))))}
        catch{$out=ConvertTo-Json -InputObject @{error=$_.Exception.Message} -Compress;[Console]::Out.Write('ERR:'+([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($out))));exit 2}
        """;
        var start=new ProcessStartInfo{FileName="powershell.exe",UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=Encoding.UTF8,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-ExecutionPolicy");start.ArgumentList.Add("Bypass");start.ArgumentList.Add("-Command");start.ArgumentList.Add(script);
        using var process=Process.Start(start)??throw new InvalidOperationException("无法启动 Windows PowerShell 模型传输备用通道");
        using var registration=cancellation.Register(()=>{try{process.Kill(true);}catch(InvalidOperationException){}});
        var input=JsonSerializer.Serialize(new{endpoint=settings.ValidateEndpoint().ToString(),key=settings.GetKey(),payload=Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))});
        await process.StandardInput.WriteAsync(input.AsMemory(),cancellation);process.StandardInput.Close();
        var outputTask=process.StandardOutput.ReadToEndAsync(cancellation);var errorTask=process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);var output=(await outputTask).Trim();_ = await errorTask;
        if(!output.StartsWith("OK:",StringComparison.Ordinal)){
            var detail="PowerShell model transport failed";
            if(output.StartsWith("ERR:",StringComparison.Ordinal))try{detail=JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(output[4..])))?["error"]?.ToString()??detail;}catch(FormatException){}
            throw new HttpRequestException(detail);
        }
        var root=JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(output[3..])))!;
        var message=root["choices"]?[0]?["message"]?.AsObject()??throw new InvalidDataException("模型响应没有 message");
        var calls=message["tool_calls"] as JsonArray??new JsonArray();
        return new(message["content"]?.GetValue<string>()??"",message,calls,timer.ElapsedMilliseconds,root["usage"]?.ToJsonString()??"unavailable");
    }

    public async Task<ModelReply> TestToolCalling(ModelSettings settings, CancellationToken cancellation)
    {
        // Force a harmless observation call and deliberately do not dispatch it to the game.
        var messages=new JsonArray { new JsonObject { ["role"]="user",["content"]="这是桌面端兼容性测试。按要求调用 observe 工具一次，不要在自然语言中作答。" } };
        var choice=new JsonObject { ["type"]="function",["function"]=new JsonObject { ["name"]="observe" } };
        var reply=await Complete(settings,messages,cancellation,choice);
        var probe=reply.Calls.FirstOrDefault(c=>c?["function"]?["name"]?.GetValue<string>()=="observe")
            ??throw new InvalidDataException("模型接口接受了请求，但没有返回受强制选择的 observe 工具调用");
        var arguments=probe["function"]?["arguments"]?.GetValue<string>()??"{}";
        using var parsed=JsonDocument.Parse(arguments);
        if(parsed.RootElement.ValueKind!=JsonValueKind.Object||parsed.RootElement.EnumerateObject().Any())
            throw new InvalidDataException("observe 探测返回了无效参数；未执行任何游戏动作");
        return reply;
    }
}

public static class ToolCatalog
{
    public static JsonArray SeasonMaintenanceDefinitions()=>new()
    {
        Tool("update_season_progress","只追加上一日已验证的游戏进度；不得修改季节计划固定字段",("progress","array","从已提供的成功工具回执中提炼的进度字符串数组")),
        Tool("create_season_plan","在首个季节计划缺失时创建，或季节切换且上一季已核验后创建下一季计划",("season","string","当前观察中的季节名"),("goal","string","本季目标"),("constraints","array","锁定约束字符串数组"),("successCriteria","array","可由游戏状态与回执核验的成功条件字符串数组"),("progress","array","初始化进度字符串数组"),("previousSeasonReview","string","季末基于成功条件、累计进度与真实回执核验上一季的结果；首次创建时说明这是初始计划"))
    };
    public static readonly HashSet<string> Queries=new(new[]{"observe","inspect_target","inspect_route","inspect_area","inspect_targets","read_mailbox"});
    public static readonly HashSet<string> Actions=new(new[]{"water_area","harvest_area","refill_water","forage_area","clear_area","till_area","plant_area","deposit_items","withdraw_items","go_to","follow_player","return_home","sleep","respond_dialogue","fish_at","fight_enemies","mine_travel"});
    public static readonly string[] TopLevelFrequentTools={"go_to","water_area","harvest_area","forage_area","sleep","clear_area","till_area","plant_area","deposit_items","withdraw_items"};
    public static void ValidateArguments(string name,JsonObject arguments)
    {
        var definition=PrimitiveDefinitions().FirstOrDefault(t=>t?["function"]?["name"]?.ToString()==name)?["function"]?["parameters"]
            ??throw new InvalidOperationException("未注册的工具："+name);
        using var schema=JsonDocument.Parse(definition.ToJsonString());using var value=JsonDocument.Parse(arguments.ToJsonString());
        ContractValidation.ValidateSchema(schema.RootElement,value.RootElement,name);
    }
    private static JsonObject Field(string type,string description)=>type=="array"
        ?new(){["type"]=type,["description"]=description,["items"]=new JsonObject{["type"]="string"}}
        :new(){["type"]=type,["description"]=description};
    private static JsonObject Tool(string name,string description,params (string Name,string Type,string Description)[] fields)
    {
        var props=new JsonObject();var required=new JsonArray();
        foreach(var f in fields){props[f.Name]=Field(f.Type,f.Description);required.Add(f.Name);}
        return new(){["type"]="function",["function"]=new JsonObject{["name"]=name,["description"]=description,["parameters"]=new JsonObject{["type"]="object",["properties"]=props,["required"]=required,["additionalProperties"]=false}}};
    }
    public static JsonArray PrimitiveDefinitions(bool soloTest=false)
    {
        var a=new JsonArray
        {
            Tool("list_capabilities","展开除矿洞外其他已开放功能；下一轮提供完整技能参数"),
            Tool("read_mailbox","读取本玩家未读邮件的正文模板和附件描述；只读，不领取附件或改变原版已读状态"),
            Tool("observe","读取当前原生游戏观察"),
            Tool("inspect_target","检查当前地图格子与箱子内容",("x","integer","横坐标"),("y","integer","纵坐标")),
            Tool("inspect_route","检查当前可用的步行连接；exitTile属于fromMap且会触发离图，arrivalTile属于toMap。跨地图时直接把go_to的location和坐标设为最终目的地图及到达点，不要先go_to当前地图的出口格再调用下一张地图",("location","string","机器地点标识")),
            Tool("inspect_area","只读检查当前地图指定区域的地形、障碍和可达性",("location","string","地点标识"),("x","integer","左上横坐标"),("y","integer","左上纵坐标"),("width","integer","宽1到32"),("height","integer","高1到32")),
            Tool("read_context_history","按需读取当前存档的本地历史；不会操作游戏。plans空id列计划索引，指定id读步骤与回执；memory空id列记忆，指定id读原文；receipts指定任务id分页读处理格与掉落",("source","string","plans、memory、receipts或diagnostics"),("id","string","计划ID、记忆ID或任务ID；查询索引时为空字符串"),("offset","integer","从0开始的分页偏移"),("limit","integer","每页1到8项")),
            Tool("go_to","普通步行并自动跨地图到达最终地点和格子；location和坐标应是最终目的地，经过地图出口由导航处理，不要把当前地图exitTile作为中间目标（触发出口会立即切图）。collect=true时顺路拾取路径周围可见掉落物，单个掉落物拾取失败不会中断寻路",("location","string","最终目的地图标识；跨地图时直接填写目标地图"),("x","integer","最终地图中的到达横坐标"),("y","integer","最终地图中的到达纵坐标"),("collect","boolean","是否顺路采集，默认false"),("pickupRadius","integer","采集范围距实际A*路线的格数，0到8，默认2"),("breakNaturalObstacles","boolean","是否允许破坏自然障碍以缩短路径，由模型决定")),
            Tool("respond_dialogue","读取当前弹窗后按明确选项继续；普通提示用空responseKey确认",("responseKey","string","必须是当前弹窗实际提供的responseKey；非选项提示填空字符串")),
            Tool("fish_at","在指定岸边和抛竿方向连续钓鱼；本地控制原版鱼条，结果只按原版鱼获确认",("location","string","已观察的钓鱼地点机器标识"),("x","integer","玩家应站立的岸边格X"),("y","integer","玩家应站立的岸边格Y"),("direction","string","抛竿朝向"),("casts","integer","最多抛竿次数1到20"),("treasurePolicy","string","try或ignore，由模型选择是否争取宝箱")),
            Tool("fight_enemies","在当前普通矿井连续战斗，按原版武器碰撞与怪物状态核验；不自动吃食物或撤退",("maxKills","integer","击杀目标数1到30"),("maxGameMinutes","integer","游戏时间上限1到600"),("weaponId","string","明确使用的武器QualifiedItemId")),
            Tool("mine_travel","在普通矿井入口、电梯或已出现梯子间通行；不挖矿、不生成梯子、不自动战斗",("targetFloor","integer","目标普通矿井楼层0到120；0表示离开矿井")),
            Tool("follow_player","跟随联机玩家，在不支持地图外等待",("playerId","string","玩家机器标识")),
            Tool("refill_water","在当前地图寻找可达水源补水"),Tool("return_home","步行回自己的小屋"),Tool("sleep","自动从当前位置步行回自己的农舍、前往床并等待原版换日；无需先单独调用return_home或go_to"),
            Tool("cancel_task","取消当前任务，不代表批准任何请求"),
            Tool("ask_user","仅缺少必要授权或观察与记忆均无法取得关键信息时询问；已授权的日常经营自行选择；未回答不代表批准",("question","string","中文问题")),
            Tool("remember_experience","记录可复用的工作经验，包括条件、依据和下一步",("text","string","条件、经验、来源及下一步核验")),
            Tool("update_project","更新项目下一步建议",("projectId","string","项目机器 ID"),("nextStep","string","基于当前观察的下一步建议"))
        };
        var goTo=a.First(t=>t?["function"]?["name"]?.GetValue<string>()=="go_to");
        goTo!["function"]!["parameters"]!["properties"]!["breakNaturalObstacles"]=new JsonObject{["type"]="boolean",["description"]="是否允许为缩短路程破坏自然枝条、石块、杂草、野树或草；会消耗工具耐久/体力并掉落物品，由模型决定"};
        foreach(var name in new[]{"water_area","harvest_area","forage_area"})
            if(name=="water_area")a.Add(Tool(name,"浇灌指定区域当前仍需浇水的作物；可选择自动前往最近水源补满后继续，已浇水格会跳过",("location","string","地点标识"),("x","integer","区域左上横坐标"),("y","integer","区域左上纵坐标"),("width","integer","宽1到120"),("height","integer","高1到120"),("autoRefill","boolean","水量不足时是否自动前往最近可达水源补满并继续，默认false")));
            else a.Add(Tool(name,"按指定区域连续处理目标，并返回实际执行结果",("location","string","地点标识"),("x","integer","区域左上横坐标"),("y","integer","区域左上纵坐标"),("width","integer","宽1到120"),("height","integer","高1到120")));
        foreach(var tool in a)if(tool?["function"]?["name"]?.GetValue<string>()=="water_area"){
            tool["function"]!["parameters"]!["properties"]!["autoRefill"]!["default"]=false;
            var required=tool["function"]!["parameters"]!["required"]!.AsArray();required.Remove(required.First(v=>v!.ToString()=="autoRefill"));
        }
        a.Add(Tool("clear_area","按类型连续清除指定区域的自然障碍，并按真实状态验收",("location","string","地点标识"),("x","integer","左上横坐标"),("y","integer","左上纵坐标"),("width","integer","宽1到32"),("height","integer","高1到32"),("targetType","string","障碍类型")));
        a.Add(Tool("till_area","将指定区域的可耕空地锄成耕地，不播种",("location","string","地点标识"),("x","integer","左上横坐标"),("y","integer","左上纵坐标"),("width","integer","宽1到32"),("height","integer","高1到32")));
        a.Add(Tool("plant_area","在指定区域已有的空耕地播种指定种子",("location","string","地点标识"),("x","integer","左上横坐标"),("y","integer","左上纵坐标"),("width","integer","宽1到32"),("height","integer","高1到32"),("seedId","string","背包种子QualifiedItemId")));
        foreach(var name in new[]{"deposit_items","withdraw_items"})
            a.Add(Tool(name,"自动步行到指定的玩家授权工作箱、打开并按堆叠批量存取明确数量；保护物品不能转移",("chest","string","已授权工作箱名称"),("itemId","string","物品机器标识，如 (O)388"),("count","integer","数量1到9999")));
        foreach(var tool in a)if(tool?["function"]?["name"]?.GetValue<string>() is "clear_area" or "till_area" or "plant_area"){
            tool["function"]!["parameters"]!["properties"]!["width"]!["maximum"]=32;
            tool["function"]!["parameters"]!["properties"]!["height"]!["maximum"]=32;
        }
        foreach(var tool in a)
            if(tool?["function"]?["name"]?.GetValue<string>()=="clear_area"){
                tool["function"]!["parameters"]!["properties"]!["targetType"]=new JsonObject{["type"]="string",["enum"]=new JsonArray("all","debris","twig","stone","weeds","grass","tree","stump"),["description"]="tree砍成熟野树并拾取木材；twig只处理地面枝条；stump处理小树桩；debris清石枝草但保留树；all也包含野树"};
                tool["function"]!["parameters"]!["properties"]!["removeStumps"]=new JsonObject{["type"]="boolean",["description"]="砍树后是否继续处理树桩；省略时处理，false保留树桩"};
            }
        foreach(var c in BusinessCapabilities.All){
            a.Add(new JsonObject{["type"]="function",["function"]=new JsonObject{["name"]=c.Name,["description"]=c.Description,
                ["parameters"]=new JsonObject{["type"]="object",["properties"]=JsonNode.Parse(c.PropertiesJson),["required"]=new JsonArray(c.Required.Select(k=>(JsonNode?)JsonValue.Create(k)).ToArray()),["additionalProperties"]=false}}});
        }
        foreach(var tool in a){var f=tool!["function"]!;var props=f["parameters"]!["properties"]!.AsObject();var name=f["name"]!.ToString();
            foreach(var coordinate in new[]{"x","y"})if(props[coordinate]!=null)props[coordinate]!["minimum"]=0;
            if(name.EndsWith("_area"))foreach(var dimension in new[]{"width","height"}){props[dimension]!["minimum"]=1;props[dimension]!["maximum"]=32;props[dimension]!["description"]="区域尺寸1到32格";}
            if(name=="go_to"){
                var required=f["parameters"]!["required"]!.AsArray();foreach(var key in new[]{"collect","pickupRadius","breakNaturalObstacles"})required.Remove(required.First(v=>v!.ToString()==key));
                props["collect"]!["default"]=false;props["pickupRadius"]!["default"]=2;props["pickupRadius"]!["minimum"]=0;props["pickupRadius"]!["maximum"]=8;props["breakNaturalObstacles"]!["default"]=false;
            }
            if(name=="fish_at"){
                props["casts"]!["minimum"]=1;props["casts"]!["maximum"]=20;
                props["direction"]!["enum"]=new JsonArray("up","right","down","left");props["treasurePolicy"]!["enum"]=new JsonArray("try","ignore");
            }
            if(name=="fight_enemies"){props["maxKills"]!["minimum"]=1;props["maxKills"]!["maximum"]=30;props["maxGameMinutes"]!["minimum"]=1;props["maxGameMinutes"]!["maximum"]=600;}
            if(name is "deposit_items" or "withdraw_items"){props["count"]!["minimum"]=1;props["count"]!["maximum"]=9999;}
            if(name=="read_context_history"){props["source"]!["enum"]=new JsonArray("plans","memory","receipts","diagnostics");props["offset"]!["minimum"]=0;props["limit"]!["minimum"]=1;props["limit"]!["maximum"]=8;}
        }
        return a;
    }
    public static JsonArray Definitions(bool soloTest=false,CapabilityStatus[]? capabilities=null)
    {
        var primitives=PrimitiveDefinitions(soloTest);
        var planningProperties=new JsonObject{["planId"]=Field("string","unique planning id"),["goal"]=Field("string","public subgoal"),["basis"]=Field("string","observed facts and public rationale")};
        primitives.Add(new JsonObject{["type"]="function",["function"]=new JsonObject{["name"]="save_plan",["description"]="记录当前目标和依据；后续步骤与进度由Core根据工具计划及执行回执维护",["parameters"]=new JsonObject{["type"]="object",["properties"]=planningProperties,["required"]=new JsonArray("planId","goal","basis"),["additionalProperties"]=false}}});
        var tools=new JsonArray();var grouped=BusinessCapabilities.Groups.ToDictionary(g=>g,_=>new JsonArray());
        foreach(var t in primitives){
            var f=t!["function"]!;var name=f["name"]!.GetValue<string>();
            if(name=="mine_travel")continue;
            if(BusinessCapabilities.Find(name) is {} business){
                var status=capabilities?.FirstOrDefault(c=>c.Name==name);
                var alwaysExpose=name is "craft_items" or "craft_and_place";
                var exposed=capabilities==null||alwaysExpose||status?.Validated==true||status?.State is "supported" or "available_unverified";
                if(name!="mine_travel"&&exposed){if(business.Query)tools.Add(t.DeepClone());else AddVariant(grouped[business.Group],name,f["parameters"]!);}
                continue;
            }
            if(Actions.Contains(name)){
                AddVariant(grouped[BusinessCapabilities.GroupFor(name)],name,f["parameters"]!);
                if(TopLevelFrequentTools.Contains(name))tools.Add(t.DeepClone());
            }else tools.Add(t.DeepClone());
        }
        foreach(var group in BusinessCapabilities.Groups){var variants=grouped[group];if(variants.Count==0)continue;var title=BusinessCapabilities.GroupTitle(group);
            tools.Add(new JsonObject{["type"]="function",["function"]=new JsonObject{
                ["name"]="group_"+group,["description"]=title+"：在本组选择1到8个按序执行的子技能；失败时停止后续步骤。只填写planId和steps，不添加计划说明字段",
                ["parameters"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{
                    ["planId"]=Field("string","本次计划唯一标识，相同标识不会重复执行"),
                    ["encounterPolicy"]=new JsonObject{["type"]="string",["enum"]=new JsonArray("fight","avoid","interrupt"),["description"]="遇到普通矿井怪物时的策略；省略时默认interrupt"},
                    ["steps"]=new JsonObject{["type"]="array",["minItems"]=1,["maxItems"]=8,["items"]=new JsonObject{["oneOf"]=variants}}},
                    ["required"]=new JsonArray("planId","steps"),["additionalProperties"]=false}}});
        }
        tools.Add(Tool("wait_for_change","没有有效工作时等待状态变化或到期",("seconds","integer","1到120秒"),("reason","string","明确等待什么条件")));
        tools.Last()!["function"]!["parameters"]!["properties"]!["seconds"]!["minimum"]=1;
        tools.Last()!["function"]!["parameters"]!["properties"]!["seconds"]!["maximum"]=120;
        tools.Add(new JsonObject{["type"]="function",["function"]=new JsonObject{["name"]="inspect_targets",["description"]="一次检查当前地图1到32格，避免逐格模型往返",
            ["parameters"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{["tiles"]=new JsonObject{["type"]="array",["minItems"]=1,["maxItems"]=32,["items"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{["x"]=Field("integer","tile coordinate"),["y"]=Field("integer","tile coordinate")},["required"]=new JsonArray("x","y"),["additionalProperties"]=false}}},["required"]=new JsonArray("tiles"),["additionalProperties"]=false}}});
        return tools;
    }
    private static void AddVariant(JsonArray variants,string name,JsonNode schema)=>variants.Add(new JsonObject{["type"]="object",["properties"]=new JsonObject{
        ["skill"]=new JsonObject{["type"]="string",["enum"]=new JsonArray(name)},["arguments"]=schema.DeepClone()},["required"]=new JsonArray("skill","arguments"),["additionalProperties"]=false});

}
