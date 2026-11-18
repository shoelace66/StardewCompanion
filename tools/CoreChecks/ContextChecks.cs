using System.Text.Json.Nodes;
using StardewCompanion.Core;

internal static class ContextChecks
{
    private static void Check(bool condition,string message){if(!condition)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);}
    internal static void Run(string folder)
    {
        var journalPath=Path.Combine(folder,"resume-journal.jsonl");
        new DecisionJournal(journalPath).Write("save","old","","before_restart",new{});
        File.AppendAllText(journalPath,"{incomplete-event}\n");
        var resumedJournal=new DecisionJournal(journalPath);resumedJournal.Write("save","new","","after_restart",new{});
        Check(resumedJournal.Read().Single().Sequence==2,"resumed journal continues event IDs after a restart and ignores incomplete records");
        using(var db=new MemoryStore(Path.Combine(folder,"context-history.db"))){
            var oldId=db.Add("archive","agreement","很早的用户约定","player");
            for(var i=0;i<510;i++)db.Add("archive","routine","历史经验"+i,"model");
            Check(db.ReadHistory("archive",oldId.ToString(),0,1).Entries.Single().Text=="很早的用户约定"&&db.ReadHistory("archive","",510,1).Entries.Single().Id==oldId,
                "paginated archive can retrieve memories older than the normal 500-item working window");
            Check(db.ReadHistory("other-save",oldId.ToString(),0,1).Total==0,"archive retrieval cannot cross save/character scope");
            var project=db.CreateProject("archive","跨日建筑项目",0);
            var projectGuid=DecisionContextCompression.Text(DecisionContextCompression.Decode(JsonValue.Create(db.ReadHistory("archive",project.ToString(),0,1).Entries.Single().Text)),"id");
            Check(db.ReadHistory("archive",projectGuid,0,1).Entries.Single().Id==project,"project summary IDs resolve to original local memory documents");
        }
    }
}
