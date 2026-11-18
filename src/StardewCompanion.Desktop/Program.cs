using System.Windows;
using System.IO;

namespace StardewCompanion.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app=new Application();
        if(args.Length==2&&args[0]=="--monitor"){app.Run(new DecisionMonitorWindow(Path.GetFullPath(args[1])));return;}
        var dataIndex=Array.IndexOf(args,"--data");
        var data=dataIndex>=0&&dataIndex+1<args.Length?Path.GetFullPath(args[dataIndex+1]):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"StardewCompanion");
        if(args.Length==2&&args[0]=="--spring-progress"){app.Run(new SpringTrialWindow(Path.GetFullPath(args[1])));return;}
        var workspaceIndex=Array.IndexOf(args,"--workspace");
        var workspace=workspaceIndex>=0&&workspaceIndex+1<args.Length?Path.GetFullPath(args[workspaceIndex+1]):null;
        int? trialDays=null;var trialIndex=Array.IndexOf(args,"--trial-days");
        if(trialIndex>=0){if(trialIndex+1>=args.Length||!int.TryParse(args[trialIndex+1],out var parsed)||parsed is <1 or >112)throw new ArgumentException("--trial-days must be 1..112");trialDays=parsed;}
        app.Run(new MainWindow(data,trialDays,workspace));
    }
}
