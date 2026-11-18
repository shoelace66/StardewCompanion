using System.Runtime.CompilerServices;
using StardewValley.Quests;

namespace StardewCompanion.Mod;

// Native daily quests may have no content ID. This handle follows that exact
// quest instance from the board into the player's log; re-observe after reload.
internal static class QuestIdentity
{
    private sealed class Handle { internal readonly string Value="observed-quest:"+Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<Quest,Handle> Handles=new();
    internal static string? For(Quest? quest)=>quest==null?null:!string.IsNullOrWhiteSpace(quest.id.Value)?quest.id.Value:Handles.GetValue(quest,_=>new Handle()).Value;
}
