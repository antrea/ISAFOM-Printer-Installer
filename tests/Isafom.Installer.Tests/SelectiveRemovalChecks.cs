using System.Windows;
using System.Windows.Controls;
using Isafom.Windows;

internal static class SelectiveRemovalChecks
{
    internal static SelectiveQueue[] Inventory => [
        new("00000000-0000-0000-0000-000000000001","ISAFOM principale","Kyocera TASKalfa 3554ci KX","IP_1","192.0.2.1","Offline","ISAFOM",true,"",true,new string('A',64)),
        new("00000000-0000-0000-0000-000000000002","ISAFOM copia","Kyocera TASKalfa 3554ci KX","IP_2","192.0.2.1","Incompleta","ISAFOM — incompleta",true,"",true,new string('B',64)),
        new("00000000-0000-0000-0000-000000000003","PDF di sistema","Microsoft Print to PDF","PORTPROMPT:","","Normal","Altra stampante",false,"Stampante di sistema",false,new string('C',64)),
        new("00000000-0000-0000-0000-000000000004","ISAFOM predefinita","Kyocera TASKalfa 3554ci KX","IP_1","192.0.2.1","Normal","ISAFOM",true,"",true,new string('D',64),true)
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
        check(dialog.QueueChoices.Count==4&&!dialog.QueueChoices[2].IsEnabled&&dialog.QueueChoices[3].IsEnabled&&dialog.QueueChoices[3].IsChecked==false,"System printers stay protected; default ISAFOM queue is selectable without preselection");
        check(((TextBlock)dialog.QueueChoices[3].Content).Text.Contains("Predefinita Windows"),"Inventory identifies the selectable default printer");
        dialog.DefaultAcknowledgement.IsChecked=true;
        check(!dialog.RemoveButton.IsEnabled,"Default acknowledgement cannot bypass selection and review");
        dialog.QueueChoices[3].IsChecked=true;
        dialog.ReviewAsync().GetAwaiter().GetResult();
        check(dialog.ApprovedPlan?.RemovesDefaultPrinter==true&&dialog.ApprovedPlan.Description.Contains("ATTENZIONE")&&dialog.ApprovedPlan.Description.Contains("Predefinita Windows: sì")&&dialog.DefaultAcknowledgement.Visibility==Visibility.Visible&&dialog.DefaultAcknowledgement.IsChecked==false,"Reviewed default removal carries a warning and requires fresh dedicated acknowledgement");
        dialog.Acknowledgement.IsChecked=true;
        check(!dialog.RemoveButton.IsEnabled,"General confirmation alone cannot authorize default printer removal");
        dialog.DefaultAcknowledgement.IsChecked=true;
        check(dialog.RemoveButton.IsEnabled,"Both explicit confirmations enable removal of the selected default queue");
        dialog.QueueChoices[3].IsChecked=false;
        check(dialog.ApprovedPlan is null&&dialog.DefaultAcknowledgement.IsChecked==false&&dialog.DefaultAcknowledgement.Visibility==Visibility.Collapsed&&!dialog.RemoveButton.IsEnabled,"Selection changes invalidate dedicated default confirmation");
        dialog.QueueChoices[0].IsChecked=true;dialog.ReviewAsync().GetAwaiter().GetResult();dialog.Acknowledgement.IsChecked=true;
        check(dialog.DefaultAcknowledgement.Visibility==Visibility.Collapsed&&dialog.RemoveButton.IsEnabled,"Plans without the default queue require only the usual confirmation");
        dialog.RefreshAsync().GetAwaiter().GetResult();
        check(loads==1&&dialog.QueueChoices.All(c=>c.IsChecked==false)&&dialog.ApprovedPlan is null,"Refresh reads a new inventory without restarting and clears all selections");
        dialog.Close();
        var other=Inventory[3] with { Associated=false, Association="Altra Kyocera — associazione ISAFOM non verificata", PortName="Porta errata" };
        SelectiveQueue[] mixed=[Inventory[0],other,Inventory[2]];
        var batch=new SelectiveRemovalWindow(()=>Task.FromResult(mixed),ids=>Task.FromResult(new SelectiveRemovalPlan(mixed.Where(q=>ids.Contains(q.QueueId)).ToArray(),[],[],[],[],new string('F',64))));
        batch.SetInventory(mixed);
        check(batch.QueueChoices.Count==2&&batch.QueueChoices.All(c=>c.IsEnabled&&c.IsChecked==false),"Unassociated local Kyocera queues are visible and selectable alongside ISAFOM queues, with no preselection");
        foreach(var choice in batch.QueueChoices)choice.IsChecked=true;
        batch.ReviewAsync().GetAwaiter().GetResult();
        check(batch.ApprovedPlan?.Queues.Length==2&&batch.ApprovedPlan.RemovesOtherKyocera&&batch.ApprovedPlan.RemovesDefaultPrinter&&batch.ApprovedPlan.Description.Contains(other.Association)&&batch.OtherKyoceraAcknowledgement.Visibility==Visibility.Visible,"One reviewed batch binds multiple exact queue identities and flags uncertain association and default status");
        batch.Acknowledgement.IsChecked=true;batch.DefaultAcknowledgement.IsChecked=true;
        check(!batch.RemoveButton.IsEnabled,"Unassociated Kyocera batch also requires dedicated association confirmation");
        batch.OtherKyoceraAcknowledgement.IsChecked=true;
        check(batch.RemoveButton.IsEnabled,"All required confirmations enable the exact multi-printer batch");
        batch.DefaultAcknowledgement.IsChecked=false;
        check(!batch.RemoveButton.IsEnabled,"Association confirmation cannot bypass default confirmation");
        batch.ShowOthers.IsChecked=true;
        check(batch.ApprovedPlan is null&&batch.OtherKyoceraAcknowledgement.IsChecked==false&&batch.DefaultAcknowledgement.IsChecked==false&&batch.QueueChoices.All(c=>c.IsChecked==false),"Changing inventory visibility invalidates all batch confirmations and selections");
        batch.Close();
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
            // RDP may report a session default different from the original
            // user's persisted default used by the elevated removal helper.
            const string defaultKey=@"HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\Windows";
            string storedDefaultBefore=Microsoft.Win32.Registry.GetValue(defaultKey,"Device","") as string??"";
            var rows=SelectiveRemovalService.InventoryAsync(Isafom.Core.PrinterSettings.Default()).GetAwaiter().GetResult();
            var after=new WindowsMachine(Isafom.Core.PrinterSettings.Default()).InspectAsync(CancellationToken.None).GetAwaiter().GetResult();
            if(rows.Length<before.Queues.Length || System.Text.Json.JsonSerializer.Serialize(before)!=System.Text.Json.JsonSerializer.Serialize(after))throw new Exception("Read-only inventory changed system metadata");
            if(rows.Any(q=>q.CanSelect&&((!q.Associated&&!System.Text.RegularExpressions.Regex.IsMatch(q.DriverName,@"^Kyocera(?:\s|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))||!Guid.TryParse(q.QueueId,out _))))throw new Exception("Unsafe inventory row");
            if(rows.Any(q=>q.IsDefault!=storedDefaultBefore.StartsWith(q.Name+",",StringComparison.OrdinalIgnoreCase)))throw new Exception("Original-user default printer status missing from inventory");
            if(defaultBefore!=Isafom.KxSentinel.PrinterStore.DefaultName())throw new Exception("Default printer changed during read-only inventory");
            if(storedDefaultBefore!=(Microsoft.Win32.Registry.GetValue(defaultKey,"Device","") as string??""))throw new Exception("Original-user default printer changed during read-only inventory");
            Console.WriteLine($"PASS Actual embedded backend: {rows.Length} rows, identity/protection checked, system metadata unchanged; no mutation");
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
