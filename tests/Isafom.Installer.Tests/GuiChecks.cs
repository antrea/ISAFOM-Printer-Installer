using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Isafom.Core;
using Isafom.Windows;
using Isafom.Collector;

internal static class GuiChecks
{
    public static int Run(string output)
    {
        int passed=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
        try
        {
            Directory.CreateDirectory(output);
            var app=new Application();var events=new SafeEventLog();var window=new MainWindow(events);
            var root=(FrameworkElement)window.Content;root.Resources.MergedDictionaries.Add(window.Resources);
            System.Windows.Documents.TextElement.SetFontFamily(root,window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(root,window.FontSize);root.Language=window.Language;
            window.Content=null;
            void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
            void Layout(double w,double h,double dpi=1)
            {
                root.Measure(new(w,h));root.Arrange(new(0,0,w,h));root.UpdateLayout();Pump();
                window.ApplyAdaptiveLayout(new(w,h),dpi);root.UpdateLayout();Pump();root.UpdateLayout();
            }
            void Render(string name,int pixelsW,int pixelsH,double dpi=1)
            {
                Layout(pixelsW/dpi,pixelsH/dpi,dpi);
                var bitmap=new RenderTargetBitmap(pixelsW,pixelsH,96*dpi,96*dpi,PixelFormats.Pbgra32);bitmap.Render(root);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name));png.Save(file);
            }
            bool Unclipped(FrameworkElement element,FrameworkElement stop)
            {
                if(element.ActualWidth<=0||element.ActualHeight<=0)return false;
                for(DependencyObject? parent=VisualTreeHelper.GetParent(element);parent is not null;parent=VisualTreeHelper.GetParent(parent))
                {
                    if(parent is FrameworkElement bounds && (bounds.ClipToBounds || bounds is ScrollContentPresenter || ReferenceEquals(bounds,stop)))
                    {
                        var rectangle=element.TransformToAncestor(bounds).TransformBounds(new Rect(element.RenderSize));
                        if(rectangle.Left < -1 || rectangle.Top < -1 || rectangle.Right > bounds.ActualWidth+1 || rectangle.Bottom > bounds.ActualHeight+1)return false;
                    }
                    if(ReferenceEquals(parent,stop))return true;
                }
                return false;
            }
            bool TextLineFits(Control input)
            {
                input.ApplyTemplate();
                if(input.Template.FindName("PART_ContentHost",input) is not ScrollViewer host)return false;
                return host.ViewportHeight+1>=input.FontFamily.LineSpacing*input.FontSize &&
                    host.ExtentHeight<=host.ViewportHeight+1;
            }
            var steps=window.JourneySteps.Items.Cast<JourneyStep>().ToArray();
            Check(steps.Length==5&&steps.All(s=>s.State==JourneyStepState.Pending),"Initial roadmap makes no completion claims");
            Check(window.Icon is not null,"WPF window loads the compiled multi-size icon resource");
            Check(window.SupportContact.Text.Contains("Andrea Esposito")&&window.SupportContact.Text.Contains("andrea.esposito@cnr.it"),"Requested name and email appear together in the support footer");
            Check(window.HeaderSubtitle.Text.Contains("TASKalfa 3554ci KX"),"Full KX model name is visible");
            window.UserName.Text="ÀÉÎÒÜ D'Ángelo gjpqy";window.Account.Password="0073";
            Layout(920,660);
            Check(TextLineFits(window.UserName)&&TextLineFits(window.Account),"Actual text viewports fit full accented text line and password bullets");
            var firstCharacter=window.UserName.GetRectFromCharacterIndex(0);
            var lastCharacter=window.UserName.GetRectFromCharacterIndex(window.UserName.Text.Length-1);
            Check(!firstCharacter.IsEmpty&&!lastCharacter.IsEmpty&&firstCharacter.Top>=0&&lastCharacter.Bottom<=window.UserName.ActualHeight,"First and last character geometry stays inside the editable control");
            Render("Installer_CampiCompilati_023.png",920,660);
            Render("Installer_GUI_023.png",920,660);
            window.BeginJourney(false);window.SetBusy(true);
            foreach(var stage in new[]{Stage.Inspecting,Stage.Network,Stage.Driver,Stage.Preferences})events.Record(stage);
            Check(steps.Take(3).All(s=>s.State==JourneyStepState.Completed)&&steps[3].State==JourneyStepState.Active&&steps[4].State==JourneyStepState.Pending,"Engine events advance only the reached phases");
            Check(window.InstallationLog.Text.Contains("Job Accounting")&&window.DebugLog.Text.Contains("stage=Preferences"),"Both logs receive live events");
            Check(!window.InstallButton.IsEnabled&&window.Progress.Visibility==Visibility.Visible,"Busy state blocks duplicate execution");
            Check(!window.UninstallButton.IsEnabled,"Installation and uninstall cannot run together");
            Render("Installer_InCorso_023.png",920,660);
            events.Record(Stage.Preferences,ErrorCode.Verification);
            Check(steps[3].State==JourneyStepState.Failed&&steps[4].State==JourneyStepState.Pending,"Error leaves later phases pending");
            window.SetBusy(false);window.BeginJourney(true);window.SetBusy(true);events.Record(Stage.Verification);events.Record(Stage.Finished);
            Check(steps.Take(4).All(s=>s.State==JourneyStepState.NotRun)&&steps[4].State==JourneyStepState.Completed,"Standalone verification does not claim installation work");
            window.SetBusy(false);window.BeginJourney(false);window.SetBusy(true);
            Task.Run(()=>events.Record(Stage.Network)).GetAwaiter().GetResult();Pump();
            Check(steps[1].State==JourneyStepState.Active,"Background progress reaches the WPF dispatcher");
            for(int i=0;i<220;i++)events.Record(Stage.Preferences);
            Layout(920,660);
            Check(window.InstallationLog.Text.Split(Environment.NewLine).Length==200,"Live log stays bounded");
            Check(window.InstallationLog.VerticalOffset>=window.InstallationLog.ExtentHeight-window.InstallationLog.ViewportHeight-2,"Live log follows its last line");

            FrameworkElement[] critical=[window.UserName,window.Account,window.RevealButton,window.InstallButton,window.VerifyButton,window.JourneySteps,window.StatusCard,window.InstallationLog,window.SupportContact];
            var monitors=new[]{new Size(1280,720),new Size(1366,768),new Size(1920,1080),new Size(2560,1440),new Size(3840,2160)};
            foreach(double dpi in new[]{1d,1.25,1.5,2})
            foreach(var monitor in monitors)
            {
                // Reserve taskbar and native chrome before converting physical pixels to DIPs.
                var viewport=new Size((monitor.Width-24)/dpi,(monitor.Height-48-Math.Ceiling(38*dpi))/dpi);
                var work=new PixelBounds(-1600,40,(int)monitor.Width,(int)monitor.Height-48);
                var fit=WindowGeometry.Fit(work,dpi,null,920,710,true);
                Check(fit.Left>=work.Left&&fit.Top>=work.Top&&fit.Right<=work.Right&&fit.Bottom<=work.Bottom,"Native window fits monitor "+monitor+" DPI "+dpi);
                foreach(string state in new[]{"ready","busy","error"})
                {
                    window.SetBusy(state=="busy");
                    window.StatusTitle.Text=state=="error"?"Operazione non completata":"Operazione in corso";
                    window.StatusText.Text=state=="error"?Messages.For(ErrorCode.ProviderNotValidated):InstallationJourney.Message(Stage.Preferences);
                    Layout(viewport.Width,viewport.Height,dpi);
                    var sizing=AdaptiveLayout.Fit(viewport,dpi);
                    bool inside=critical.All(control=>Unclipped(control,window.AppSurface));
                    bool onScreen=sizing.RequiresScrolling||critical.All(control=>Unclipped(control,root));
                    bool fieldsComplete=window.FieldsScroll.ExtentHeight<=window.FieldsScroll.ViewportHeight+1;
                    bool scaleCorrect=Math.Abs(window.UiScale.ScaleX-window.UiScale.ScaleY)<.001&&window.UiScale.ScaleX<=1&&window.UiScale.ScaleX*dpi>=.919;
                    Check(inside&&onScreen&&fieldsComplete&&scaleCorrect&&TextLineFits(window.UserName)&&TextLineFits(window.Account),"No control or typed-text clipping "+state+" "+monitor+" DPI "+dpi);
                }
                window.Pages.SelectedIndex=1;Layout(viewport.Width,viewport.Height,dpi);
                Check(Unclipped(window.CaptureButton,window.AppSurface)&&Unclipped(window.UninstallButton,window.AppSurface)&&Unclipped(window.DebugSummary,window.AppSurface)&&Unclipped(window.DebugLog,window.AppSurface),"Debug fits complete surface "+monitor+" DPI "+dpi);
                window.Pages.SelectedIndex=0;
            }
            // Reproduce the reported 1918x1000 screenshot at 150%, including busy state.
            window.SetBusy(true);window.StatusText.Text=InstallationJourney.Message(Stage.Driver);
            Render("Installer_FHD_150_023.png",1918,966,1.5);
            Render("Installer_HD_200_023.png",1280,596,2);
            window.SetBusy(false);
            Layout(340,280,1);
            Check(window.RootScroll.ScrollableHeight>0&&window.RootScroll.ScrollableWidth>0,"Tiny viewport uses outer scrolling instead of unreadable scaling");
            window.RootScroll.ScrollToBottom();window.RootScroll.ScrollToRightEnd();Pump();root.UpdateLayout();
            Check(window.RootScroll.VerticalOffset>0&&window.RootScroll.HorizontalOffset>0,"Overflow surface remains reachable by scrolling");
            var removalPlan=new RemovalPlan("Kyocera TASKalfa 3554ci KX",true,0,true,"Kyocera TASKalfa 3554ci KX","8.5.2405.0",true,true,[],true,null,new string('A',64),
                PortName:"stampante.isafom.cnr.it",Packages:[new("oem42.inf",new string('B',64))],DownloadCachePresent:true,SettingsFilePresent:true,
                DriverRegistrations:[new("Kyocera TASKalfa 3554ci KX","Windows x64"),new("Kyocera CS 3554ci KX","Windows x64")]);
            var confirmation=new RemovalWindow(removalPlan);
            var confirmationRoot=(FrameworkElement)confirmation.Content;confirmation.Content=null;
            System.Windows.Documents.TextElement.SetFontFamily(confirmationRoot,confirmation.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(confirmationRoot,confirmation.FontSize);
            ((Panel)confirmationRoot).Background=confirmation.Background;
            confirmationRoot.Measure(new(570,440));confirmationRoot.Arrange(new Rect(0,0,570,440));confirmationRoot.UpdateLayout();
            Check(Unclipped(confirmation.RemoveButton,confirmationRoot),"Removal confirmation keeps its action button visible with a scrolling description");
            var confirmationBitmap=new RenderTargetBitmap(570,440,96,96,PixelFormats.Pbgra32);confirmationBitmap.Render(confirmationRoot);
            var confirmationPng=new PngBitmapEncoder();confirmationPng.Frames.Add(BitmapFrame.Create(confirmationBitmap));
            using(var confirmationFile=File.Create(Path.Combine(output,"Disinstallazione_Anteprima_025.png")))confirmationPng.Save(confirmationFile);
            confirmation.Close();
            var forceInventory=SelectiveRemovalChecks.Inventory;
            forceInventory[1]=forceInventory[1] with { Associated=false, Association="Altra Kyocera — associazione ISAFOM non verificata", PortName="Porta errata" };
            var forceConfirmation=new SelectiveRemovalWindow(()=>Task.FromResult(forceInventory),
                ids=>Task.FromResult(new SelectiveRemovalPlan(forceInventory.Where(q=>ids.Contains(q.QueueId)).ToArray(),[],[],[],[],new string('F',64))));
            forceConfirmation.SetInventory(forceInventory);
            var forceRoot=(FrameworkElement)forceConfirmation.Content;forceConfirmation.Content=null;
            System.Windows.Documents.TextElement.SetFontFamily(forceRoot,forceConfirmation.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(forceRoot,forceConfirmation.FontSize);
            ((Panel)forceRoot).Background=forceConfirmation.Background;
            forceRoot.Measure(new(660,620));forceRoot.Arrange(new Rect(0,0,660,620));forceRoot.UpdateLayout();
            Check(Unclipped(forceConfirmation.RemoveButton,forceRoot)&&!forceConfirmation.RemoveButton.IsEnabled,"Selective force actions remain visible and require explicit selection");
            var forceBitmap=new RenderTargetBitmap(660,620,96,96,PixelFormats.Pbgra32);forceBitmap.Render(forceRoot);
            var forcePng=new PngBitmapEncoder();forcePng.Frames.Add(BitmapFrame.Create(forceBitmap));
            using(var forceFile=File.Create(Path.Combine(output,"Disinstallazione_Forzata_027.png")))forcePng.Save(forceFile);
            forceConfirmation.QueueChoices[1].IsChecked=true;forceConfirmation.QueueChoices[2].IsChecked=true;
            forceConfirmation.ReviewAsync().GetAwaiter().GetResult();
            forceRoot.Measure(new(660,620));forceRoot.Arrange(new Rect(0,0,660,620));forceRoot.UpdateLayout();
            ((DockPanel)forceRoot).Children.OfType<ScrollViewer>().Single().ScrollToBottom();Pump();forceRoot.UpdateLayout();
            Check(Unclipped(forceConfirmation.Acknowledgement,forceRoot)&&Unclipped(forceConfirmation.DefaultAcknowledgement,forceRoot)&&Unclipped(forceConfirmation.OtherKyoceraAcknowledgement,forceRoot)&&Unclipped(forceConfirmation.RemoveButton,forceRoot)&&!forceConfirmation.RemoveButton.IsEnabled,"Multiple queue review keeps all dedicated confirmations reachable by scrolling and actions visible");
            var batchBitmap=new RenderTargetBitmap(660,620,96,96,PixelFormats.Pbgra32);batchBitmap.Render(forceRoot);
            var batchPng=new PngBitmapEncoder();batchPng.Frames.Add(BitmapFrame.Create(batchBitmap));
            using(var batchFile=File.Create(Path.Combine(output,"Disinstallazione_SelezioneMultipla_027.png")))batchPng.Save(batchFile);
            forceConfirmation.Close();
            window.RootScroll.ScrollToHome();Pump();
            window.UserName.Text="  ";window.Account.Password="0073";
            window.InstallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.Account.Password=="0073"&&window.Revealed.Text.Length==0,"Retained code stays masked after validation error");
            string payload=string.Join('\n',window.DebugLog.Text.Split(Environment.NewLine).Select(line=>line.Contains(" stage=",StringComparison.Ordinal)?line[(line.IndexOf(" stage=",StringComparison.Ordinal)+1)..]:line));
            Check(!window.InstallationLog.Text.Contains("0073")&&!payload.Contains("0073"),"Neither log discloses the entered code");
            Render("Installer_Compatto_023.png",760,580,1.25);
            window.Account.Clear();window.UserName.Clear();window.Pages.SelectedIndex=1;
            Render("Installer_Debug_023.png",920,660);
            window.Close();Check(window.Account.Password.Length==0,"Closing clears the retained code");app.Shutdown();
            Console.WriteLine($"RESULT {passed} GUI/DPI checks passed; no printer changes or print jobs.");return 0;
        }
        catch(Exception error){Console.WriteLine("FAIL GUI: "+error.GetType().Name+" "+error.Message);return 1;}
    }
}
