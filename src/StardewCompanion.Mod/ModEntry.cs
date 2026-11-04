using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewCompanion.Protocol;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewCompanion.Mod;
using Microsoft.Xna.Framework;

namespace StardewCompanion.Mod;

public sealed class ModEntry : StardewModdingAPI.Mod
{
    private BridgeClient? _bridge;
    private string _session = Guid.NewGuid().ToString("N");
    private long _revision;
    private bool _paused = true;
    private GameObservation? _taskBefore;
    private TaskCommand? _active;
    private SButton _button;
    private long _endsAt;
    private RoutineExecutor? _routine;
    private RoutineExecutor? _cleanup;
    private int? _sleepLockedDay;
    private readonly Dictionary<string, TaskResult> _results = new();
    private readonly Queue<string> _resultOrder = new();
    private TilePosition? _start;
    private bool _wasHealthy;
    private readonly AiInputSimulator _input = new();
    private static bool _nativeRightClick;
    private static bool _nativeToolClick;
    private static bool _nativeToolInput;
    private static bool _manualControl;
    private bool? _mouseVisibilityBeforeTool;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public override void Entry(IModHelper helper)
    {
        if (Environment.GetEnvironmentVariable("SC_AI_CLIENT") != "1")
        {
            Monitor.Log("Bridge disabled: this process is not designated as the AI client.", LogLevel.Info);
            return;
        }
        var endpoint = Environment.GetEnvironmentVariable("SC_BRIDGE_ENDPOINT");
        var token = Environment.GetEnvironmentVariable("SC_BRIDGE_TOKEN");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token))
        { Monitor.Log("Missing bridge endpoint or token; no input control will run.", LogLevel.Error); return; }
        GameEventTracker.Configure(Helper.DirectoryPath);
        _bridge = new BridgeClient(endpoint, token);
        var eventHarmony=new Harmony(ModManifest.UniqueID+".events");
        eventHarmony.Patch(AccessTools.Method(typeof(Farmer),nameof(Farmer.passOutFromTired)),prefix:new HarmonyMethod(typeof(ModEntry),nameof(ObservePassOut)));
        eventHarmony.Patch(AccessTools.Method(typeof(GameLocation),nameof(GameLocation.answerDialogueAction)),postfix:new HarmonyMethod(typeof(ModEntry),nameof(ObserveSleep)));
        helper.Events.GameLoop.UpdateTicking += OnTicking;
        helper.Events.GameLoop.UpdateTicked += OnTicked;
        helper.Events.GameLoop.SaveLoaded += (_, _) => ResetWorld();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ResetWorld();
        helper.Events.GameLoop.DayStarted += (_, _) => { _sleepLockedDay = null; _bridge.Send("day_started", _session, new { date = Game1.Date.TotalDays }); };
        // The vanilla control loop is gated on IsActiveNoOverlay even in multiplayer.
        // Patch ONLY this opted-in AI process; movement/collision/tool rules are unchanged.
        new Harmony(ModManifest.UniqueID).Patch(AccessTools.PropertyGetter(typeof(Game1), nameof(Game1.IsActiveNoOverlay)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(AllowAiBackgroundInput)));
        new Harmony(ModManifest.UniqueID).Patch(AccessTools.Method(typeof(Character), nameof(Character.GetToolLocation), new[] { typeof(Vector2), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(UseAiFacingForTool)));
        new Harmony(ModManifest.UniqueID).Patch(AccessTools.Method(typeof(Game1), nameof(Game1.didPlayerJustRightClick)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(SyntheticRightClickEdge)));
        new Harmony(ModManifest.UniqueID).Patch(AccessTools.Method(typeof(Game1), nameof(Game1.didPlayerJustLeftClick)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(SyntheticToolClickEdge)));
        new Harmony(ModManifest.UniqueID).Patch(AccessTools.Method(typeof(BobberBar), nameof(BobberBar.update)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BobberBarInputPrefix)),
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(BobberBarInputPostfix)));
        new Harmony(ModManifest.UniqueID).Patch(AccessTools.Method(typeof(Utility), nameof(Utility.canGrabSomethingFromHere)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(PreserveToolIntent)));
        helper.ConsoleCommands.Add("sc_status", "Report M0 bridge status", (_, _) => Monitor.Log(JsonSerializer.Serialize(Observe()), LogLevel.Info));
        Monitor.Log("M0 bridge enabled. Starts paused; only explicit diagnostic tasks are available.", LogLevel.Info);
    }

    private static void ObservePassOut(Farmer who)=>GameEventTracker.PassOut(who);
    private static void ObserveSleep(string questionAndAnswer,bool __result)
    {if(__result&&questionAndAnswer=="Sleep_Yes")GameEventTracker.SleepRequested();}
    private void ResetWorld()
    {
        Finish("cancelled", "world_changed");
        _session = Guid.NewGuid().ToString("N");
        _paused = true;
        _results.Clear();
        _resultOrder.Clear();
        _sleepLockedDay = null;
        _manualControl=false;
    }

    private static bool AllowAiBackgroundInput(ref bool __result)
    {
        if (!Context.IsWorldReady||_manualControl) return true;
        __result = true;
        return false;
    }

    private static void UseAiFacingForTool(Character __instance, ref Vector2 target_position, ref bool ignoreClick)
    {
        if (!Context.IsWorldReady || _manualControl || !ReferenceEquals(__instance, Game1.player)) return;
        // The human's OS cursor belongs to the human. Preserve the normal facing-based tool reach.
        target_position = Vector2.Zero;
        ignoreClick = true;
    }

    private static bool SyntheticRightClickEdge(ref bool __result)
    {
        if (!_nativeRightClick) return true;
        __result = true;
        return false;
    }
    private static bool SyntheticToolClickEdge(ref bool __result)
    {
        if(!_nativeToolClick)return true;
        __result=true;return false;
    }
    private static void BobberBarInputPrefix(out BobberMouseState __state)
    {
        var overridden = SpecialActivityExecutor.OverrideNativeInput(out var beforeMouse,out var beforeKeyboard);
        __state = new BobberMouseState(overridden, beforeMouse,beforeKeyboard);
    }
    private static void BobberBarInputPostfix(BobberMouseState __state)
    {
        if (__state.Overridden) SpecialActivityExecutor.RestoreNativeInput(__state.PreviousMouse,__state.PreviousKeyboard);
    }
    private readonly record struct BobberMouseState(bool Overridden, MouseState PreviousMouse,KeyboardState PreviousKeyboard);
    private static bool PreserveToolIntent(Farmer who,ref bool __result)
    {
        if(!_nativeToolInput||!ReferenceEquals(who,Game1.player))return true;
        // Game1 checks grab actions after firing a tool. A watering command must not also harvest a ripe crop.
        __result=false;return false;
    }
    private void PrepareNativeToolInput()
    {
        if(!_input.Tool)return;
        _nativeToolInput=true;
        // Keyboard-style targeting must not depend on the human's cursor in a different window.
        _mouseVisibilityBeforeTool=Game1.wasMouseVisibleThisFrame;Game1.wasMouseVisibleThisFrame=false;
        _nativeToolClick=Game1.player.CurrentTool is StardewValley.Tools.MeleeWeapon weapon&&weapon.isScythe();
    }
    private void RestoreToolInput()
    {
        _nativeToolClick=false;
        _nativeToolInput=false;
        if(_mouseVisibilityBeforeTool is {} visible)Game1.wasMouseVisibleThisFrame=visible;
        _mouseVisibilityBeforeTool=null;
    }

    private void OnTicking(object? sender, UpdateTickingEventArgs e)
    {
        if (_bridge == null) return;
        RestoreToolInput();
        var healthy = _bridge.Healthy;
        if(Context.IsWorldReady && _cleanup?.Cleanup()!=false)_cleanup=null;
        if (_wasHealthy && !healthy) { _paused = true; Finish("cancelled", "bridge_lost"); }
        _wasHealthy = healthy;
        while (_bridge.TryRead(out var envelope))
        {
            try
            {
                if(envelope.Type=="events_ack")
                {if(envelope.SessionId==_session){using var ack=JsonDocument.Parse(envelope.Payload);GameEventTracker.Acknowledge(ack.RootElement.GetProperty("ids").EnumerateArray().Select(v=>v.GetString()!).ToArray());}continue;}
                if (envelope.Type == "pause") { _paused = true; Finish("cancelled", "paused"); Emit(envelope.MessageId, "completed", "pause_ack"); }
                else if (envelope.Type == "resume")
                {
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - envelope.SentAtUtcMs > 5000 || envelope.SessionId != _session)
                    { Emit(envelope.MessageId, "rejected", "expired_resume"); continue; }
                    _paused = false; Emit(envelope.MessageId, "completed", "resume_ack");
                }
                else if (envelope.Type == "cancel") { Finish("cancelled", "cancelled_by_player"); Emit(envelope.MessageId, "completed", "cancel_ack"); }
                else if (envelope.Type == "task")
                {
                    TaskCommand? command = null;
                    try
                    {
                        command = JsonSerializer.Deserialize<TaskCommand>(envelope.Payload, JsonOptions);
                        if (command == null || string.IsNullOrWhiteSpace(command.TaskId)) throw new JsonException("Missing task identity");
                        Accept(command);
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
                    {
                        Emit(command?.TaskId ?? envelope.MessageId, "rejected", "invalid_arguments");
                    }
                }
            }
            catch (Exception ex)
            { Monitor.Log("Invalid diagnostic command: " + ex.Message, LogLevel.Warn); }
        }
        if (!Context.IsWorldReady) return;
        if(SoloTestMode.Enabled && Game1.activeClickableMenu is LevelUpMenu level && !level.isProfessionChooser)
            level.receiveGamePadButton(Buttons.B);
        // Keep background simulation active, without saving any user option changes.
        Game1.options.pauseWhenOutOfFocus = false;
        Helper.Reflection.GetField<StardewValley.Util.IInputSimulator>(typeof(Game1), "inputSimulator").SetValue(_input);
        _input.Clear();
        _manualControl=_input.ManualControl=_paused&&SoloTestMode.Enabled;
        if(_manualControl)return;
        foreach (var key in Keyboard.GetState().GetPressedKeys())
            if (Enum.TryParse<SButton>(key.ToString(), out var physical)) Helper.Input.Suppress(physical);
        Helper.Input.Suppress(SButton.MouseLeft);
        Helper.Input.Suppress(SButton.MouseRight);
        if (!healthy || _paused || _active == null) return;
        if (_routine != null)
        {
            try
            {
                var status = _routine.Tick(_input);
                PrepareNativeToolInput();
                if (status=="running" && _routine.Phase == "waiting_for_next_day") _sleepLockedDay = Game1.Date.TotalDays;
                if (status != "running") { Finish(status, _routine.Reason); return; }
                if (_routine.ProgressDue) { Emit(_active.TaskId, "progress", _routine.Phase, JsonSerializer.Serialize(_routine.BusinessDetails), _routine.Completed); _routine.ProgressSent(); }
            }
            catch (Exception ex) { Monitor.Log("Routine stopped: " + ex, LogLevel.Warn); Finish("failed", "routine_exception:"+ex.GetType().Name+":"+ex.Message); }
            return;
        }
        if (Environment.TickCount64 >= _endsAt) { Finish("completed", "input_finished"); return; }
        if (_active.Skill == "walk" && Game1.activeClickableMenu != null) { Finish("failed", "menu_open"); return; }
        if (_active.Skill == "walk")
            _input.Direction = _button switch { SButton.W => "up", SButton.S => "down", SButton.A => "left", SButton.D => "right", _ => "" };
        else if (_active.Skill == "use_tool") _input.Tool = true;
        else if (_active.Skill == "interact") _input.Interact = true;
        else Helper.Input.Press(_button);
        PrepareNativeToolInput();
    }

    private void OnTicked(object? sender, UpdateTickedEventArgs e)
    {
        RestoreToolInput();
        if (_bridge != null && e.IsMultipleOf(15)) {GameEventTracker.Poll();_bridge.Send("observation", _session, Observe());}
    }

    private GameObservation Observe()
    {
        var ready = Context.IsWorldReady;
        return new GameObservation
        {
            Revision = ++_revision,
            SessionId = _session,
            WorldReady = ready,
            SaveId = ready ? Game1.uniqueIDForThisGame.ToString() : "",
            PlayerId = ready ? Game1.player.UniqueMultiplayerID : 0,
            PlayerName = ready ? Game1.player.Name : "",
            IsHost = ready && Context.IsMainPlayer,
            Multiplayer = ready && Context.IsMultiplayer,
            ControlMode=SoloTestMode.Enabled?"solo_test":"farmhand",
            TestRunId=SoloTestMode.Enabled?SoloTestMode.RunId:"",
            Location = ready ? Game1.player.currentLocation.NameOrUniqueName : "",
            Tile = ready ? new TilePosition { X = Game1.player.TilePoint.X, Y = Game1.player.TilePoint.Y } : new(),
            Stamina = ready ? Game1.player.Stamina : 0,
            Health = ready ? Game1.player.health : 0,
            TimeOfDay = ready ? Game1.timeOfDay : 0,
            GameDay = ready ? Game1.Date.TotalDays : 0,
            Menu = Game1.activeClickableMenu?.GetType().Name ?? "",
            MenuContext = ready ? DescribeMenuContext() : "{}",
            Paused = _paused,
            ActiveTaskId = _active?.TaskId ?? "",
            ConnectedPlayers = ready ? Game1.getOnlineFarmers().Select(f => $"{f.UniqueMultiplayerID}:{f.Name}").ToArray() : Array.Empty<string>(),
            WorldView = ready ? JsonSerializer.Serialize(WorldView.Read()) : "{}",
            Capabilities=ready?BusinessAvailability.Read().Concat(SpecialActivityExecutor.Capabilities()).ToArray():Array.Empty<CapabilityStatus>(),
            DiagnosticDetails = ready ? JsonSerializer.Serialize(new {
                pixel = new { x = Game1.player.Position.X, y = Game1.player.Position.Y },
                canMove = Game1.player.CanMove, usingTool = Game1.player.UsingTool,
                inBed = Game1.player.isInBed.Value, facing = Game1.player.FacingDirection,
                toolIndex = Game1.player.CurrentToolIndex,
                inventory = Game1.player.Items.Select((item, slot) => new { slot, id = item?.QualifiedItemId, name = item?.Name, stack = item?.Stack ?? 0 }),
                exits = Game1.currentLocation.warps.Select(w => new { x = w.X, y = w.Y, target = w.TargetName, targetX = w.TargetX, targetY = w.TargetY }),
                objects = Game1.currentLocation.objects.Pairs.Select(p => new { x = p.Key.X, y = p.Key.Y, name = p.Value.Name }).Take(100)
            }) : "{}"
        };
    }

    private static string DescribeMenuContext()
    {
        if(Game1.activeClickableMenu is not DialogueBox dialogue)
            return JsonSerializer.Serialize(new{type=Game1.activeClickableMenu?.GetType().Name??"",text="",question=false,choices=Array.Empty<object>()});
        var text=dialogue.getCurrentString()??"";
        if(text.Length>2000)text=text[..2000];
        var choices=dialogue.responses.Select(r=>new{responseKey=r.responseKey,text=r.responseText}).ToArray();
        return JsonSerializer.Serialize(new{type="DialogueBox",text,question=dialogue.isQuestion,lastQuestionKey=Game1.currentLocation.lastQuestionKey,choices});
    }

    private void Accept(TaskCommand command)
    {
        if (_results.TryGetValue(command.TaskId, out var prior)) { _bridge!.Send("result", _session, prior); return; }
        string? reason = !Context.IsWorldReady ? "world_not_ready"
            : command.SessionId != _session || command.SaveId != Game1.uniqueIDForThisGame.ToString() || command.PlayerId != Game1.player.UniqueMultiplayerID ? "identity_mismatch"
            : Context.IsMainPlayer && !SoloTestMode.Enabled ? "ai_must_be_farmhand"
            : !_bridge!.Healthy ? "bridge_unhealthy"
            : command.ExpiresAtUtcMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ? "expired_command"
            : command.ObservationRevision > _revision || _revision - command.ObservationRevision > 20 ? "stale_observation" : null;
        if (reason != null) { Emit(command.TaskId, "rejected", reason); return; }
        using var args = JsonDocument.Parse(command.Arguments);
        if(command.Skill=="mine_travel"){Emit(command.TaskId,"rejected","skill_disabled:mine_travel",skill:command.Skill);return;}
        foreach(var field in new[]{"location","destination","toLocation"})
            if(args.RootElement.TryGetProperty(field,out var destination)&&destination.ValueKind==JsonValueKind.String){
                var name=destination.GetString()??"";
                if(name is "Mine" or "SkullCave" or "SkullCavern" or "VolcanoDungeon" or "BugLand"||name.StartsWith("UndergroundMine",StringComparison.Ordinal)){
                    Emit(command.TaskId,"rejected","location_disabled:"+name,skill:command.Skill);return;
                }
            }
        if(BusinessCapabilities.Find(command.Skill) is {} business){
            ContractValidation.Validate(business,args.RootElement,true);
            if(!BusinessAvailability.AllowedForDirectLabExecution(command.Skill)){Emit(command.TaskId,"rejected","capability_not_validated_or_unavailable",skill:command.Skill);return;}
            if(business.Query){Emit(command.TaskId,"completed","observation",JsonSerializer.Serialize(BusinessAvailability.Survey(args.RootElement)),skill:command.Skill);return;}
        }
        if (command.Skill is "observe" or "inspect_target" or "inspect_targets" or "inspect_route" or "inspect_area" or "read_mailbox")
        {
            object details = command.Skill switch
            {
                "read_mailbox" => GameEventTracker.ReadMailbox(),
                "inspect_target" => WorldView.Inspect(args.RootElement.GetProperty("x").GetInt32(), args.RootElement.GetProperty("y").GetInt32()),
                "inspect_route" => new { knowledge = "observed_connections", route = Navigation.Route(Game1.currentLocation.NameOrUniqueName, args.RootElement.GetProperty("location").GetString()!)?.Select(e=>new{
                    fromMap=e.From,toMap=e.To,exitTile=new{x=e.Tile.X,y=e.Tile.Y},arrivalTile=e.ArrivalTile is {} arrival?new{x=arrival.X,y=arrival.Y}:null,
                    interact=e.Interact,coordinateMeaning="exitTile belongs to fromMap; arrivalTile belongs to toMap"
                }).ToArray() },
                "inspect_area" => BusinessAvailability.Survey(args.RootElement),
                "inspect_targets" => WorldView.InspectMany(args.RootElement),
                _ => WorldView.Read()
            };
            Emit(command.TaskId, "completed", "observation", JsonSerializer.Serialize(details),skill:command.Skill); return;
        }
        if (_paused || _active != null || _sleepLockedDay == Game1.Date.TotalDays)
        { Emit(command.TaskId, "rejected", _paused ? "paused" : _active != null ? "busy" : "waiting_for_next_day"); return; }
        if(_cleanup!=null) { Emit(command.TaskId,"rejected","container_cleanup_pending");return; }
        if (command.Skill == "bind_chest")
        {
            var tile = new Vector2(args.RootElement.GetProperty("x").GetInt32(), args.RootElement.GetProperty("y").GetInt32());
            if (!Game1.currentLocation.objects.TryGetValue(tile, out var obj) || obj is not StardewValley.Objects.Chest chest || !WorldView.WorkChest(chest))
            { Emit(command.TaskId, "rejected", "not_supported_work_chest"); return; }
            if (!obj.modData.TryGetValue(WorldView.ChestKey, out var id)) obj.modData[WorldView.ChestKey] = id = Guid.NewGuid().ToString("N");
            Emit(command.TaskId, "completed", "chest_bound", JsonSerializer.Serialize(new { id, location = Game1.currentLocation.NameOrUniqueName, x = (int)tile.X, y = (int)tile.Y }),skill:command.Skill); return;
        }
        if (RoutineExecutor.Skills.Contains(command.Skill))
        {
            _routine = new RoutineExecutor(command.Skill, command.Arguments, NativeInteract, command.EncounterPolicy);
            _active = command; _taskBefore=Observe();_start = _taskBefore.Tile;
            Emit(command.TaskId, "accepted", "routine_started"); return;
        }
        var duration = 60;
        switch (command.Skill)
        {
            case "face":
                var directionValue = args.RootElement.GetProperty("direction").GetInt32();
                if (directionValue < 0 || directionValue > 3) { Emit(command.TaskId, "rejected", "invalid_direction"); return; }
                Game1.player.faceDirection(directionValue);
                Emit(command.TaskId, "completed", "facing_set");
                return;
            case "menu_click":
                if (Game1.activeClickableMenu is not ItemGrabMenu itemMenu) { Emit(command.TaskId, "rejected", "not_container_menu"); return; }
                var panel = args.RootElement.GetProperty("panel").GetString();
                var inventoryMenu = panel switch { "container" => itemMenu.ItemsToGrabMenu, "player" => itemMenu.inventory, _ => null };
                var itemSlot = args.RootElement.GetProperty("slot").GetInt32();
                if (inventoryMenu == null || itemSlot < 0 || itemSlot >= inventoryMenu.inventory.Count)
                { Emit(command.TaskId, "rejected", "invalid_menu_slot"); return; }
                var click = inventoryMenu.inventory[itemSlot].bounds.Center;
                itemMenu.receiveLeftClick(click.X, click.Y);
                Emit(command.TaskId, "completed", "native_menu_click");
                return;
            case "select_item":
                var slot = args.RootElement.GetProperty("slot").GetInt32();
                if (slot < 0 || slot >= Game1.player.Items.Count || Game1.player.Items[slot] == null)
                { Emit(command.TaskId, "rejected", "invalid_slot"); return; }
                Game1.player.CurrentToolIndex = slot;
                Emit(command.TaskId, "completed", "selected_item");
                return;
            case "walk":
                var direction = args.RootElement.GetProperty("direction").GetString();
                _button = direction switch { "up" => SButton.W, "down" => SButton.S, "left" => SButton.A, "right" => SButton.D, _ => SButton.None };
                duration = args.RootElement.TryGetProperty("milliseconds", out var ms) ? ms.GetInt32() : 300;
                if (duration < 1 || duration > 1500 || _button == SButton.None) { Emit(command.TaskId, "rejected", "invalid_walk"); return; }
                break;
            case "use_tool": _button = SButton.C; break;
            case "interact":
                var toolTarget = Game1.player.GetToolLocation(ignoreClick: true);
                var interactionTile = new Vector2((int)(toolTarget.X / 64), (int)(toolTarget.Y / 64));
                if (args.RootElement.TryGetProperty("x", out var ix) && args.RootElement.TryGetProperty("y", out var iy))
                    interactionTile = new Vector2(ix.GetInt32(), iy.GetInt32());
                bool handled;
                // Chest.checkForAction requires an actual right-click edge in addition to actionButtonPressed.
                // Scope that synthetic edge to this native call; don't hold a mouse button or move the OS cursor.
                try { _nativeRightClick = true; handled = Game1.tryToCheckAt(interactionTile, Game1.player); }
                finally { _nativeRightClick = false; }
                Emit(command.TaskId, handled ? "completed" : "failed", handled ? "native_interaction_dispatched" : "interaction_not_handled");
                return;
            case "menu_confirm":
                if (Game1.activeClickableMenu is not DialogueBox dialogue || Game1.currentLocation.lastQuestionKey != "Sleep"
                    || !Game1.player.isInBed.Value || !ReferenceEquals(Utility.getHomeOfFarmer(Game1.player), Game1.currentLocation))
                { Emit(command.TaskId, "rejected", "not_own_bed_sleep_question"); return; }
                var response = Array.FindIndex(dialogue.responses, r => r.responseKey == "Yes");
                if (response < 0) { Emit(command.TaskId, "rejected", "no_sleep_yes_response"); return; }
                dialogue.selectedResponse = response;
                dialogue.receiveLeftClick(0, 0);
                Emit(command.TaskId, "completed", "native_sleep_dialogue_click");
                return;
            case "menu_cancel":
                if (Game1.activeClickableMenu == null) { Emit(command.TaskId, "rejected", "no_menu"); return; }
                Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                Emit(command.TaskId, "completed", "native_menu_cancel");
                return;
            default: Emit(command.TaskId, "rejected", "unsupported_m0_skill"); return;
        }
        _active = command;
        _start = Observe().Tile;
        _endsAt = Environment.TickCount64 + duration;
        Emit(command.TaskId, "accepted", "m0_diagnostic_input");
    }

    private static void NativeInteract(GridPoint tile)
    {
        try { _nativeRightClick = true; Game1.tryToCheckAt(new Vector2(tile.X, tile.Y), Game1.player); }
        finally { _nativeRightClick = false; }
    }

    private void Finish(string status, string reason)
    {
        if (_active == null) return;
        var task = _active;
        if(status is "cancelled" or "rejected")GameEventTracker.Add("action_interrupted",new{taskId=task.TaskId,skill=task.Skill,reason});
        _routine?.CaptureCompleted();
        var completed = _routine?.Completed ?? 0;
        var collectedItems=_routine?.CollectedItems;
        var affectedTiles=_routine?.AffectedTiles??Array.Empty<object>();
        var businessDetails=_routine?.BusinessDetails;
        var remainingDrops=_routine?.RemainingDrops();
        if(_routine?.Cleanup()==false)_cleanup=_routine;
        _routine = null;
        _active = null;
        _input.Clear();
        _button = SButton.None;
        var after = Observe();
        var moved = _start != null && (_start.X != after.Tile.X || _start.Y != after.Tile.Y);
        Emit(task.TaskId, task.Skill == "walk" && status == "completed" && !moved ? "failed" : status,
            task.Skill == "walk" && status == "completed" && !moved ? "no_tile_displacement" : reason,
            JsonSerializer.Serialize(new { requestedTarget=JsonSerializer.Deserialize<JsonElement>(task.Arguments),playerStart = _start, playerEnd = after.Tile, after.Location,collectedItems,remainingDrops,
                affectedTiles,
                businessDetails,
                actualCost=new{staminaBefore=_taskBefore?.Stamina,staminaAfter=after.Stamina,netStaminaUsed=_taskBefore==null?(float?)null:_taskBefore.Stamina-after.Stamina,
                    healthBefore=_taskBefore?.Health,healthAfter=after.Health,gameTimeBeforeHHMM=_taskBefore?.TimeOfDay,gameTimeAfterHHMM=after.TimeOfDay,
                    gameTimeBeforeDisplay=_taskBefore==null?null:$"{_taskBefore.TimeOfDay/100:D2}:{_taskBefore.TimeOfDay%100:D2}",gameTimeAfterDisplay=$"{after.TimeOfDay/100:D2}:{after.TimeOfDay%100:D2}",
                    gameDayBefore=_taskBefore?.GameDay,gameDayAfter=after.GameDay,timeMeaning="HHMM-style game clock; 1320 means 13:20"},
                completedUnit=task.Skill=="water_area"?"tiles":task.Skill is "harvest_area" or "forage_area"?"items_collected":"skill_units" }), completed, task.Skill);
    }

    private void Emit(string taskId, string status, string reason, string details = "", int completed = 0, string skill = "")
    {
        var result = new TaskResult { TaskId = taskId, Skill = skill != "" ? skill : _active?.TaskId == taskId ? _active.Skill : "", Status = status, Reason = reason, Details = details, Completed = completed, ObservationRevision = _revision };
        if (!_results.ContainsKey(taskId)) _resultOrder.Enqueue(taskId);
        _results[taskId] = result;
        while (_resultOrder.Count > 256) _results.Remove(_resultOrder.Dequeue());
        _bridge?.Send("result", _session, result);
    }
}
