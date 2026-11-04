using Microsoft.Data.Sqlite;
using System.Text.Json;
using StardewCompanion.Protocol;

namespace StardewCompanion.Core;

public sealed partial class MemoryStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    public MemoryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()); _db.Open();
        using var version = _db.CreateCommand(); version.CommandText = "PRAGMA user_version";
        var old = Convert.ToInt32(version.ExecuteScalar());
        if (old > 1) { _db.Dispose(); throw new InvalidOperationException("数据库版本较新，已停止自动执行；请使用对应版本程序。"); }
        if (old < 1)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            { using var backup = new SqliteConnection($"Data Source={path}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak"); backup.Open(); _db.BackupDatabase(backup); }
            using var transaction = _db.BeginTransaction();
            try
            {
                using var migrate = _db.CreateCommand(); migrate.Transaction = transaction;
                migrate.CommandText = """
                CREATE TABLE memory(id INTEGER PRIMARY KEY, scope TEXT NOT NULL, kind TEXT NOT NULL, text TEXT NOT NULL, state TEXT NOT NULL, source TEXT NOT NULL, day INTEGER NOT NULL);
                CREATE INDEX memory_scope ON memory(scope,kind);
                CREATE TABLE settings(scope TEXT NOT NULL,key TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(scope,key));
                CREATE TABLE events(id INTEGER PRIMARY KEY,scope TEXT NOT NULL,session TEXT NOT NULL,day INTEGER NOT NULL,task TEXT NOT NULL,status TEXT NOT NULL,count INTEGER NOT NULL,reason TEXT NOT NULL,valid INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE chat(id INTEGER PRIMARY KEY,scope TEXT NOT NULL,role TEXT NOT NULL,text TEXT NOT NULL,at TEXT NOT NULL);
                PRAGMA user_version=1;
                """;
                migrate.ExecuteNonQuery(); transaction.Commit();
            }
            catch { transaction.Rollback(); _db.Dispose(); throw; }
        }
    }
    private SqliteCommand Command(string sql, params (string, object?)[] values)
    { var c = _db.CreateCommand(); c.CommandText = sql; foreach (var (k, v) in values) c.Parameters.AddWithValue(k, v ?? DBNull.Value); return c; }
    public long Add(string scope, string kind, string text, string source, int day = 0, string state = "unverified")
    {
        if (kind is not ("agreement" or "project" or "routine" or "place" or "episode")) throw new ArgumentException("Unknown memory kind");
        if (text.Length is < 1 or > 16000) throw new ArgumentException("Memory length");
        lock (_gate) { using var c = Command("INSERT INTO memory(scope,kind,text,state,source,day) VALUES($s,$k,$t,$v,$o,$d); SELECT last_insert_rowid();", ("$s", scope), ("$k", kind), ("$t", text), ("$v", state), ("$o", source), ("$d", day)); return (long)c.ExecuteScalar()!; }
    }
    public IReadOnlyList<MemoryEntry> List(string scope)
    {
        lock (_gate)
        { using var c = Command("SELECT id,kind,text,state,source,day FROM memory WHERE scope=$s ORDER BY id DESC LIMIT 500", ("$s", scope)); using var r = c.ExecuteReader(); var rows = new List<MemoryEntry>(); while (r.Read()) rows.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt32(5))); return rows; }
    }
    public IReadOnlyList<MemoryEntry> Relevant(string scope, string query) => List(scope).Where(m => m.State is not ("invalid" or "cancelled" or "user_confirmed_complete"))
        .OrderByDescending(m => (m.Kind is "agreement" or "project" or "place" ? 100 : 0) + query.Split(' ', '，', '。', ':').Where(t => t.Length > 1).Count(t => m.Text.Contains(t, StringComparison.OrdinalIgnoreCase))).Take(12).ToArray();
    public (long Total,MemoryEntry[] Entries) ReadHistory(string scope,string id,int offset,int limit)
    {
        if(offset<0||limit is <1 or >8)throw new ArgumentException("Invalid history page");
        var filter=id==""?"":long.TryParse(id,out _)?" AND id=$id":" AND kind='project' AND CASE WHEN json_valid(text) THEN json_extract(text,'$.Id') END=$id";
        lock(_gate){
            using var count=Command("SELECT COUNT(*) FROM memory WHERE scope=$s"+filter,("$s",scope),("$id",id));
            var total=Convert.ToInt64(count.ExecuteScalar());
            using var read=Command("SELECT id,kind,text,state,source,day FROM memory WHERE scope=$s"+filter+" ORDER BY id DESC LIMIT $limit OFFSET $offset",
                ("$s",scope),("$id",id),("$limit",limit),("$offset",offset));
            using var rows=read.ExecuteReader();var entries=new List<MemoryEntry>();
            while(rows.Read())entries.Add(new(rows.GetInt64(0),rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.GetInt32(5)));
            return (total,entries.ToArray());
        }
    }
    public void Edit(string scope, long id, string text) { lock (_gate) { using var c = Command("UPDATE memory SET text=$t,state='user_corrected',source='player' WHERE scope=$s AND id=$i", ("$t", text),("$s",scope),("$i",id)); c.ExecuteNonQuery(); } }
    public void Delete(string scope, long id) { lock (_gate) { using var c = Command("DELETE FROM memory WHERE scope=$s AND id=$i", ("$s",scope),("$i",id)); c.ExecuteNonQuery(); } }
    public void State(string scope,long id,string state) {lock(_gate){using var c=Command("UPDATE memory SET state=$v WHERE scope=$s AND id=$i",("$v",state),("$s",scope),("$i",id));c.ExecuteNonQuery();}}
    public void Set(string scope, string key, string value) { lock (_gate) { using var c = Command("INSERT INTO settings VALUES($s,$k,$v) ON CONFLICT(scope,key) DO UPDATE SET value=$v", ("$s",scope),("$k",key),("$v",value)); c.ExecuteNonQuery(); } }
    public string? Get(string scope, string key) { lock (_gate) { using var c = Command("SELECT value FROM settings WHERE scope=$s AND key=$k", ("$s",scope),("$k",key)); return c.ExecuteScalar() as string; } }
    public string? LatestChatScope()
    { lock(_gate){using var c=Command("SELECT scope FROM chat ORDER BY id DESC LIMIT 1");return c.ExecuteScalar() as string;} }
    public void Reconcile(string scope, string session, int day)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = Command("UPDATE events SET valid=0 WHERE scope=$s AND (day>$d OR session<>$session); UPDATE memory SET state='needs_recheck' WHERE scope=$s AND kind IN ('project','routine','episode') AND state NOT IN ('cancelled','user_confirmed_complete');", ("$s",scope),("$d",day),("$session",session));
            c.Transaction = tx; c.ExecuteNonQuery();
            // Row state and the model-facing project document must agree after a reload or rollback.
            var projects = new List<(long Id, ProjectNote Note)>();
            using (var read = Command("SELECT id,text FROM memory WHERE scope=$s AND kind='project' AND state='needs_recheck'", ("$s",scope)))
            {
                read.Transaction=tx;
                using var rows=read.ExecuteReader();
                while(rows.Read())
                {
                    try { if(JsonSerializer.Deserialize<ProjectNote>(rows.GetString(1)) is { } note) projects.Add((rows.GetInt64(0),note)); }
                    catch(JsonException) { }
                }
            }
            foreach(var (id,note) in projects)
            {
                var valid=new List<string>();var stale=new List<string>(note.UnverifiedResults);
                foreach(var receipt in note.VerifiedResults)
                {
                    ProjectTaskReceipt? evidence=null;
                    try { evidence=JsonSerializer.Deserialize<ProjectTaskReceipt>(receipt); } catch(JsonException) { }
                    if(evidence!=null && evidence.SessionId==session && evidence.Day<=day) valid.Add(receipt);
                    else stale.Add(receipt);
                }
                var revised=note with { VerifiedResults=valid.ToArray(),UnverifiedResults=stale.Distinct().ToArray(),Status="needs_recheck",LastCheckedSession="" };
                using var update=Command("UPDATE memory SET text=$t WHERE scope=$s AND id=$i",("$t",JsonSerializer.Serialize(revised)),("$s",scope),("$i",id));
                update.Transaction=tx;update.ExecuteNonQuery();
            }
            tx.Commit();
            Set(scope, "reconciledDay", day.ToString());
        }
    }
    public void Record(string scope, string session, int day, string task, string status, int count, string reason)
    { lock (_gate) { using var c = Command("INSERT INTO events(scope,session,day,task,status,count,reason) VALUES($s,$session,$d,$t,$status,$n,$r)", ("$s",scope),("$session",session),("$d",day),("$t",task),("$status",status),("$n",count),("$r",reason)); c.ExecuteNonQuery(); } }
    public IReadOnlyDictionary<string,int> EventSummary(string scope,string session,int day)
    {lock(_gate){using var c=Command("SELECT status,SUM(count) FROM events WHERE scope=$s AND session=$session AND day=$d AND valid=1 GROUP BY status",("$s",scope),("$session",session),("$d",day));using var r=c.ExecuteReader();var data=new Dictionary<string,int>();while(r.Read()){data[r.GetString(0)]=r.GetInt32(1);}return data;}}
    public void Chat(string scope, string role, string text) { lock (_gate) { using var c = Command("INSERT INTO chat(scope,role,text,at) VALUES($s,$r,$t,$a)", ("$s",scope),("$r",role),("$t",text),("$a",DateTime.UtcNow.ToString("O"))); c.ExecuteNonQuery(); } }
    public string[] PlayerInstructions(string scope)
    {
        var stored=Get(scope,"player_instructions");
        if(stored!=null)return (JsonSerializer.Deserialize<string[]>(stored)??Array.Empty<string>()).Where(ShouldPersistAsInstruction).ToArray();
        // Upgrade old sessions without losing the original player request after chat truncation.
        lock(_gate){using var c=Command("SELECT text FROM chat WHERE scope=$s AND role='user' ORDER BY id",("$s",scope));using var r=c.ExecuteReader();var rows=new List<string>();while(r.Read()){var text=r.GetString(0);if(ShouldPersistAsInstruction(text))rows.Add(text);}return rows.ToArray();}
    }
    public void AppendPlayerInstruction(string scope,string text)
    {
        if(!ShouldPersistAsInstruction(text))return;
        var instructions=PlayerInstructions(scope).Append(text).ToArray();
        Set(scope,"player_instructions",JsonSerializer.Serialize(instructions));
    }
    private static bool ShouldPersistAsInstruction(string text)
    {
        var value=text.Trim();
        if(value.Length==0)return false;
        if(value.Contains('?')||value.Contains('？')||value.Contains('¿'))return false;
        return !System.Text.RegularExpressions.Regex.IsMatch(value,"^(为什么|为何|怎么|怎样|是否|能否|可否|请问|你能看到|你能不能).*(吗|呢|嘛)?$",System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
    public IReadOnlyList<(string Role, string Text)> History(string scope)
    { lock (_gate) { using var c = Command("SELECT role,text FROM (SELECT id,role,text FROM chat WHERE scope=$s ORDER BY id DESC LIMIT 20) ORDER BY id", ("$s",scope)); using var r=c.ExecuteReader(); var result=new List<(string,string)>(); while(r.Read()) result.Add((r.GetString(0),r.GetString(1))); return result; } }
    public long CreateProject(string scope,string goal,int day)
    {
        var note=new ProjectNote(Guid.NewGuid().ToString("N"),goal,Array.Empty<string>(),new[]{goal},"先观察当前存档，再完成一个可核验步骤。","active","");
        return Add(scope,"project",JsonSerializer.Serialize(note),"player",day,"active");
    }
    public ProjectNote[] Projects(string scope)=>List(scope).Where(m=>m.Kind=="project"&&m.State is not ("cancelled" or "user_confirmed_complete" or "invalid")).Select(m=>{try{return JsonSerializer.Deserialize<ProjectNote>(m.Text);}catch{return null;}}).OfType<ProjectNote>().ToArray();
    public bool UpdateProjectNext(string scope,string projectId,string nextStep)
    {
        if(nextStep.Length is <1 or >2000)throw new ArgumentException("Next step length");
        var entry=List(scope).FirstOrDefault(m=>m.Kind=="project"&&(m.State is "active" or "needs_recheck")&&ProjectId(m.Text)==projectId);
        if(entry==null)return false;
        var note=JsonSerializer.Deserialize<ProjectNote>(entry.Text)!;EditRaw(scope,entry.Id,JsonSerializer.Serialize(note with{NextStep=nextStep,Status="active"}),"model_inference","needs_recheck");return true;
    }
    public bool RecordProjectResult(string scope,string projectId,string session,int day,TaskResult result)
    {
        if(result.Status is not ("completed" or "partial"))return false;
        var entry=List(scope).FirstOrDefault(m=>m.Kind=="project"&&(m.State is "active" or "needs_recheck")&&ProjectId(m.Text)==projectId);
        if(entry==null)return false;
        var note=JsonSerializer.Deserialize<ProjectNote>(entry.Text);if(note==null)return false;
        foreach(var item in note.VerifiedResults)
        {
            try{if(JsonSerializer.Deserialize<ProjectTaskReceipt>(item)?.TaskId==result.TaskId)return false;}catch(JsonException){}
        }
        var receipt=new ProjectTaskReceipt(result.TaskId,result.Skill,result.Status,result.Completed,result.Reason,day,session);
        EditRaw(scope,entry.Id,JsonSerializer.Serialize(note with{VerifiedResults=note.VerifiedResults.Append(JsonSerializer.Serialize(receipt)).ToArray(),Status="active",LastCheckedSession=session}),"game_task_result","active");
        return true;
    }
    public void ReviseProject(string scope,long id,string goal)
    {
        var entry=List(scope).FirstOrDefault(m=>m.Kind=="project"&&m.Id==id)??throw new InvalidOperationException("项目不存在");
        var note=JsonSerializer.Deserialize<ProjectNote>(entry.Text);if(note==null){CreateProject(scope,goal,entry.Day);State(scope,id,"cancelled");return;}
        EditRaw(scope,id,JsonSerializer.Serialize(note with{Goal=goal,Remaining=new[]{goal},Status="needs_recheck",LastCheckedSession=""}),"player","needs_recheck");
    }
    private static string ProjectId(string text){try{return JsonDocument.Parse(text).RootElement.GetProperty("Id").GetString()??"";}catch{return "";}}
    private void EditRaw(string scope,long id,string text,string source,string state){lock(_gate){using var c=Command("UPDATE memory SET text=$t,state=$v,source=$o WHERE scope=$s AND id=$i",("$t",text),("$v",state),("$o",source),("$s",scope),("$i",id));c.ExecuteNonQuery();}}
    public ChestBinding[] Chests(string scope) => List(scope).Where(m=>m.Kind=="place" && m.State!="invalid").Select(m=> { try { return JsonSerializer.Deserialize<ChestBinding>(m.Text); } catch { return null; } }).OfType<ChestBinding>().ToArray();
    public void Dispose() => _db.Dispose();
}
