using System.Windows;
using System.Windows.Controls;
using Isafom.Windows;

internal static class SelectiveRemovalChecks
{
    internal static SelectiveQueue[] Inventory => [
        new("00000000-0000-0000-0000-000000000001","ISAFOM principale","Kyocera TASKalfa 3554ci KX","IP_1","192.0.2.1","Offline","ISAFOM",true,"",true,new string('A',64)),
        new("00000000-0000-0000-0000-000000000002","ISAFOM copia","Kyocera TASKalfa 3554ci KX","IP_2","192.0.2.1","Incompleta","ISAFOM — incompleta",true,"",true,new string('B',64)),
        new("00000000-0000-0000-0000-000000000003","PDF di sistema","Microsoft Print to PDF","PORTPROMPT:","","Normal","Altra stampante",false,"Stampante di sistema",false,new string('C',64)),
        new("00000000-0000-0000-0000-000000000004","ISAFOM predefinita","Kyocera TASKalfa 3554ci KX","IP_1","192.0.2.1","Normal","ISAFOM",true,"Stampante predefinita protetta",false,new string('D',64),true)
    ];
    internal static SelectiveRemovalPlan Plan(string[] ids) => new(Inventory.Where(q=>ids.Contains(q.QueueId)).ToArray(),
        [new("IP_2","192.0.2.1",1,9100)],[],[],["Driver condiviso: Kyocera TASKalfa 3554ci KX"],new string('E',64));

    internal static void CheckUi(Action<bool,string> check)
    {
        int loads=0, reviews=0;
        var dialog=new SelectiveRemovalWindow(()=>{loads++;return Task.FromResult(Inventory);},ids=>{reviews++;return Task.FromResult(Plan(ids));});
        dialog.SetInventory(Inventory);
        check(dialog.QueueChoices.Count==3&&dialog.QueueChoices.All(c=>c.IsChecked==false),"ISAFOM queues highlighted without preselection; unrelated queues initially hidden");
        check(!dialog.ReviewButton.IsEnabled&&!dialog.RemoveButton.IsEnabled,"Empty selection cannot review or delete");
        dialog.Acknowledgement.IsChecked=true;
        check(!dialog.RemoveButton.IsEnabled,"Acknowledgement cannot bypass explicit selection and review");
        dialog.QueueChoices[0].IsChecked=true;dialog.QueueChoices[1].IsChecked=true;
        dialog.ReviewAsync().GetAwaiter().GetResult();
        check(reviews==1&&dialog.ApprovedPlan?.Queues.Length==2&&!dialog.RemoveButton.IsEnabled,"Review binds exactly the selected two and requires fresh confirmation");
        dialog.Acknowledgement.IsChecked=true;
        check(dialog.RemoveButton.IsEnabled&&dialog.ApprovedPlan!.Description.Contains("ID Windows")&&dialog.ApprovedPlan.Description.Contains("IP_2"),"Explicit confirmation enables deletion of the precise reviewed identities and resources");
        dialog.QueueChoices[0].IsChecked=false;
        check(dialog.ApprovedPlan is null&&dialog.Acknowledgement.IsChecked==false&&!dialog.RemoveButton.IsEnabled,"Selection changes invalidate the entire prior confirmation");
        dialog.ShowOthers.IsChecked=true;
        check(dialog.QueueChoices.Count==4&&!dialog.QueueChoices[2].IsEnabled&&!dialog.QueueChoices[3].IsEnabled,"System and default printers remain visible but protected");
        dialog.RefreshAsync().GetAwaiter().GetResult();
        check(loads==1&&dialog.QueueChoices.All(c=>c.IsChecked==false)&&dialog.ApprovedPlan is null,"Refresh reads a new inventory without restarting and clears all selections");
        dialog.Close();
        var failing=new SelectiveRemovalWindow(()=>Task.FromException<SelectiveQueue[]>(new UnauthorizedAccessException()),ids=>Task.FromResult(Plan(ids)));
        failing.SetInventory(Inventory);failing.QueueChoices[0].IsChecked=true;
        failing.RefreshAsync().GetAwaiter().GetResult();
        check(failing.QueueChoices.Count==0&&!failing.RemoveButton.IsEnabled,"Failed inventory refresh discards stale selectable rows");
        failing.Close();
        var serialized=System.Text.Json.JsonSerializer.Serialize(new MachineReply(true,null,SelectiveRemoval:[new(Inventory[0].QueueId,Inventory[0].Name,"Rimossa parzialmente",["Porta residua"],[],"")]));
        var reply=System.Text.Json.JsonSerializer.Deserialize<MachineReply>(serialized)!;
        check(reply.SelectiveRemoval is [{Complete:false}]&&reply.SelectiveRemoval[0].Description.Contains("Porta residua"),"Elevation protocol preserves individual partial outcomes and residuals");
    }
    internal static int Run()
    {
        int passed=0;
        var app=new Application();
        try {CheckUi((value,label)=>{if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);});Console.WriteLine($"RESULT {passed} selective removal UI/protocol checks passed");return 0;}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally {app.Shutdown();}
    }
    internal static int RunLive()
    {
        try
        {
            var before=new WindowsMachine(Isafom.Core.PrinterSettings.Default()).InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
            string defaultBefore=Isafom.KxSentinel.PrinterStore.DefaultName();
            var rows=SelectiveRemovalService.InventoryAsync(Isafom.Core.PrinterSettings.Default()).GetAwaiter().GetResult();
            var after=new WindowsMachine(Isafom.Core.PrinterSettings.Default()).InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
            if(rows.Length<before.Queues.Length || System.Text.Json.JsonSerializer.Serialize(before)!=System.Text.Json.JsonSerializer.Serialize(after))throw new Exception("Read-only inventory changed system metadata");
            if(rows.Any(q=>q.CanSelect&&(!q.Associated||q.IsDefault||!Guid.TryParse(q.QueueId,out _))))throw new Exception("Unsafe inventory row");
            if(defaultBefore!=Isafom.KxSentinel.PrinterStore.DefaultName())throw new Exception("Default printer changed during read-only inventory");
            Console.WriteLine($"PASS Actual embedded backend: {rows.Length} rows, identity/protection checked, system metadata unchanged; no mutation");
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
