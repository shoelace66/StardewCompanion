using System.Text.Json.Nodes;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class CompanionRuntime
{
    private string _waitExpectedChange="meaningful_state";
    private int _waitTargetTime;
    private int _waitDay;
    private string _waitLocation="";
    private bool _waitConditionTriggered;
    private JsonObject? _waitCompletion;

    private void ClearWait(){_waitDue=0;_waitFingerprint="";_waitConditionTriggered=false;}
    private void RecordWaitCompletion(GameObservation state,string status)
    {
        _waitCompletion=new JsonObject{
            ["status"]=status,["expectedChange"]=_waitExpectedChange,
            ["startedGameDay"]=_waitDay,["gameDay"]=state.GameDay,
            ["targetTime"]=_waitExpectedChange=="time_of_day"?JsonValue.Create(_waitTargetTime):null,
            ["location"] = state.Location,["timeOfDay"] = state.TimeOfDay,["menu"] = state.Menu
        };
    }
    private object StartWait(JsonObject arguments,GameObservation current)
    {
        if(current.Menu!="")throw new InvalidOperationException("modal_menu_blocks_wait:"+current.Menu+":请先处理当前界面；菜单打开时游戏时间不会推进");
        var seconds=arguments["seconds"]?.GetValue<int>()??30;
        if(seconds is <1 or >120)throw new InvalidOperationException("等待必须为1到120秒");
        var expected=arguments["expectedChange"]?.GetValue<string>()??"meaningful_state";
        var target=arguments["targetTime"]?.GetValue<int>()??0;
        if(expected=="time_of_day"&&(target<=current.TimeOfDay||target>2600||target%100>=60))
            throw new InvalidOperationException("targetTime必须为晚于当前时间且不超过2600的有效HHMM时间");
        if(expected is not ("meaningful_state" or "time_of_day" or "day_changed" or "inventory_changed" or "shop_opened"))
            throw new InvalidOperationException("unsupported_wait_condition");
        _waitCompletion=null;
        _waitExpectedChange=expected;_waitTargetTime=target;_waitDay=current.GameDay;_waitLocation=current.Location;
        _waitFingerprint=expected=="inventory_changed"?InventoryFingerprint(current):FailureState(current,"");
        _waitDue=Environment.TickCount64+seconds*1000;
        return new{status="waiting",seconds,expectedChange=expected,targetTime=expected=="time_of_day"?(int?)target:null,reason=arguments["reason"]?.GetValue<string>()};
    }
    private static string InventoryFingerprint(GameObservation state)=>Canonical(JsonNode.Parse(state.WorldView)?["inventory"])?.ToJsonString()??"null";
    private bool WaitSatisfied(GameObservation current)
    {
        if(_waitDue==0)return false;
        return _waitExpectedChange switch{
            "time_of_day"=>current.GameDay!=_waitDay||current.TimeOfDay>=_waitTargetTime,
            "day_changed"=>current.GameDay!=_waitDay,
            "inventory_changed"=>InventoryFingerprint(current)!=_waitFingerprint,
            // Only an actually observed shop menu counts, never an inferred schedule.
            "shop_opened"=>current.Location==_waitLocation&&current.Menu=="ShopMenu",
            _=>FailureState(current,"")!=_waitFingerprint
        };
    }
}
