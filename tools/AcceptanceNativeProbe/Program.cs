using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

try
{
if(args.Length<1)throw new ArgumentException("Run directory required");
var root=Path.GetFullPath(args[0]);
if(!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root),@"^SC\d+D_\d{8}_\d{6}$"))throw new InvalidOperationException("Isolated run required");
using var launch=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"launch.json")));
var pid=launch.RootElement.GetProperty("gamePid").GetInt32();
if(Process.GetProcessById(pid).ProcessName!="StardewModdingAPI")throw new InvalidOperationException("Wrong process");
using var session=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"session.json")));
var endpoint=session.RootElement.GetProperty("endpoint").GetString()!.Replace("ws:","http:").Replace("/bridge","");
using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(5)};
http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.RootElement.GetProperty("token").GetString());
using var state=JsonDocument.Parse(await http.GetStringAsync(endpoint+"/state"));
using var agent=JsonDocument.Parse(await http.GetStringAsync(endpoint+"/agent/state"));
var observation=state.RootElement;
if(!observation.GetProperty("WorldReady").GetBoolean()||observation.GetProperty("ControlMode").GetString()!="solo_test"
 ||observation.GetProperty("TestRunId").GetString()!=Path.GetFileName(root)||observation.GetProperty("PlayerName").GetString()!="SC5Host"
 ||observation.GetProperty("Multiplayer").GetBoolean()||agent.RootElement.GetProperty("running").GetBoolean())throw new InvalidOperationException("Paused isolated test identity required");
using var target=DataTarget.AttachToProcess(pid,false);
var localDac=Path.Combine(Path.GetDirectoryName(Process.GetProcessById(pid).MainModule!.FileName)!,"mscordaccore.dll");
if(!File.Exists(localDac))throw new InvalidOperationException("Matching local game DAC required; no symbol-server fallback");
var runtime=target.ClrVersions[0].CreateRuntime(localDac);
var type=runtime.EnumerateModules().First(m=>m.Name?.EndsWith("Stardew Valley.dll",StringComparison.OrdinalIgnoreCase)==true).GetTypeByName("StardewValley.Game1")!;
var domain=runtime.AppDomains[0];
var dialogue=type.GetStaticFieldByName("dialogueUp")!;
var up=dialogue.Read<bool>(domain);var eventUp=type.GetStaticFieldByName("eventUp")!.Read<bool>(domain);
var player=type.GetStaticFieldByName("_player")!.ReadObject(domain);
var location=player.ReadObjectField("currentLocationRef").ReadObjectField("_gameLocation");
var currentEvent=location.ReadObjectField("currentEvent");
var eventId=currentEvent.IsNull?null:currentEvent.ReadStringField("id");
var canMove=player.ReadField<bool>("canMove");
var repaired=false;
if(args.Contains("--ack-orphan-cave-dialogue")){
 // Only repair the known fixture-created orphaned flag, never code or arbitrary fields.
 if(!up||!eventUp||eventId!="65"||observation.GetProperty("Menu").GetString()!=""||observation.GetProperty("ActiveTaskId").GetString()!="")throw new InvalidOperationException("Known orphaned cave dialogue not present");
 var address=dialogue.GetAddress(domain);
 var handle=Native.OpenProcess(0x0020|0x0008|0x0010,false,pid);
 if(handle==IntPtr.Zero)throw new InvalidOperationException("Could not open isolated process");
 try{
  var before=new byte[1];
  if(!Native.ReadProcessMemory(handle,(IntPtr)address,before,1,out var read)||read!=1||before[0]!=1)throw new InvalidOperationException("Flag changed during verification");
  if(!Native.WriteProcessMemory(handle,(IntPtr)address,new byte[]{0},1,out var written)||written!=1)throw new InvalidOperationException("Fixture flag repair failed");
  repaired=true;
 }finally{Native.CloseHandle(handle);}
}
var result=new{fixtureOnly=true,runId=Path.GetFileName(root),pid,at=DateTimeOffset.Now,dialogueUp=up,eventUp,eventId,canMove,repaired,
 meaning="native diagnostics; optional repair acknowledges one fixture-orphaned dialogue flag; no game skill invoked or achievement claimed"};
var json=JsonSerializer.Serialize(result);
File.AppendAllText(Path.Combine(root,"native-probe.jsonl"),json+Environment.NewLine);
Console.WriteLine(json);
}
catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);Environment.ExitCode=1;}
internal static class Native
{
 [DllImport("kernel32.dll",SetLastError=true)]internal static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
 [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool ReadProcessMemory(IntPtr process,IntPtr address,byte[] buffer,nuint size,out nuint read);
 [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool WriteProcessMemory(IntPtr process,IntPtr address,byte[] buffer,nuint size,out nuint written);
 [DllImport("kernel32.dll")]internal static extern bool CloseHandle(IntPtr handle);
}
