using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HddSpindown.Core;
using HddSpindown.Windows;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace HddSpindown.App {
 public sealed partial class MainWindow : Window {
  public const string WindowTitle="HDD Spindown";
  public ObservableCollection<DiskItem> Disks {get;} = new ObservableCollection<DiskItem>();
  readonly DispatcherQueueTimer timer;
  readonly Dictionary<string,bool> lastPaused=new Dictionary<string,bool>();
  bool polling, busy, serviceReady, suspended, showFacts=true;
  Response last;
  static string LegacyFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HDD Spindown","mount-recovery.json");

  public MainWindow() {
   InitializeComponent();
   Title=Demo?WindowTitle+" · Vorschau":WindowTitle;
   ExtendsContentIntoTitleBar=true;
   SetTitleBar(AppTitleBar);
   AppWindow.TitleBar.ButtonBackgroundColor=Colors.Transparent;
   AppWindow.TitleBar.ButtonInactiveBackgroundColor=Colors.Transparent;
   try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","HDD-Spindown.ico")); } catch {}
   double scale=GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96.0;
   AppWindow.Resize(new SizeInt32((int)(1000*scale),(int)(700*scale)));
   MinSize.Attach(WinRT.Interop.WindowNative.GetWindowHandle(this),640,460);
   Root.ActualThemeChanged+=(s,e)=>ApplyCaptionColors();
   ApplyCaptionColors();
   Root.SizeChanged+=(s,e)=> { bool wide=e.NewSize.Width>=980; if(wide!=showFacts) { showFacts=wide; foreach(var d in Disks) d.ShowFacts=wide; } };
   timer=DispatcherQueue.CreateTimer(); timer.Interval=TimeSpan.FromSeconds(4); timer.Tick+=(s,e)=>_=Poll();
   timer.Start();
   LegacyMenuItem.IsEnabled=File.Exists(LegacyFile);
   InitHelp();
   Activated+=FirstActivated;
   tray=new TrayIcon(Path.Combine(AppContext.BaseDirectory,"Assets","HDD-Spindown.ico")) {Activate=ShowFromTray,BuildMenu=BuildTrayMenu};
   // Closing the window only hides it; the service keeps the locks either way.
   AppWindow.Closing+=(s,e)=> { if(!quitting) { e.Cancel=true; HideToTray(); } };
   Closed+=(s,e)=> { timer.Stop(); tray.Dispose(); };
  }
  [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

  // ---------- built-in help ----------
  static string RecoveryExe {
   get {
    string installed=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Service","HddSpindown.Recovery.exe"));
    return File.Exists(installed)?installed:@"C:\Program Files\HDD Spindown\Service\HddSpindown.Recovery.exe";
   }
  }
  string RecoveryText() {
   string exe="\""+RecoveryExe+"\"";
   return "sc stop HddSpindown\n"+exe+" status\n"+exe+" recover\nsc start HddSpindown";
  }
  void InitHelp() {
   RecoveryCommands.Text=RecoveryText();
   HelpVersion.Text="HDD Spindown "+typeof(MainWindow).Assembly.GetName().Version.ToString(3)+" · Weitere Optionen des Recovery-Werkzeugs: „list“, „import-legacy“, „export“ (Diagnosebericht).";
   var f1=new Microsoft.UI.Xaml.Input.KeyboardAccelerator {Key=global::Windows.System.VirtualKey.F1};
   f1.Invoked+=(s,e)=> { e.Handled=true; ShowHelp(true); };
   var esc=new Microsoft.UI.Xaml.Input.KeyboardAccelerator {Key=global::Windows.System.VirtualKey.Escape};
   esc.Invoked+=(s,e)=> { if(HelpView.Visibility==Visibility.Visible) { e.Handled=true; ShowHelp(false); } };
   Root.KeyboardAccelerators.Add(f1); Root.KeyboardAccelerators.Add(esc);
   Root.KeyboardAcceleratorPlacementMode=Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
  }
  void ShowHelp(bool show) {
   HelpView.Visibility=show?Visibility.Visible:Visibility.Collapsed;
   MainView.Visibility=show?Visibility.Collapsed:Visibility.Visible;
   if(show) { HelpScroll.ChangeView(null,0,null,true); HelpBack.Focus(FocusState.Programmatic); }
   else RefreshButton.Focus(FocusState.Programmatic);
  }
  void Help_Click(object sender,RoutedEventArgs e) { ShowHelp(true); }
  void HelpBack_Click(object sender,RoutedEventArgs e) { ShowHelp(false); }
  void CopyRecovery_Click(object sender,RoutedEventArgs e) { var p=new DataPackage(); p.SetText(RecoveryText().Replace("\n","\r\n")); Clipboard.SetContent(p); }

  // ---------- notification area ----------
  readonly TrayIcon tray;
  bool quitting, visible, hideNoticeShown, lastProblem;

  public void StartHidden() {
   visible=false; timer.Interval=TimeSpan.FromSeconds(15);
   DispatcherQueue.TryEnqueue(async()=>await Poll(Ops.List));
  }
  public void ShowFromTray() {
   bool first=!visible&&!everShown;
   visible=true; everShown=true; timer.Interval=TimeSpan.FromSeconds(4);
   AppWindow.Show(); Activate();
   SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
   if(!first) _=Poll();
  }
  bool everShown;
  public void MarkShown() { visible=true; everShown=true; }
  void HideToTray() {
   AppWindow.Hide(); visible=false; timer.Interval=TimeSpan.FromSeconds(15);
   if(!hideNoticeShown) { hideNoticeShown=true; tray.Notify("HDD Spindown läuft im Infobereich weiter","Pausierte HDDs bleiben pausiert – die Sperren hält der Windows-Dienst. Rechtsklick auf das Symbol für Schlafen/Freigeben."); }
  }
  void Quit() {
   quitting=true; timer.Stop();
   tray.Dispose();
   Close();
   Application.Current.Exit();
  }

  IList<TrayMenuItem> BuildTrayMenu() {
   var items=new List<TrayMenuItem> {new TrayMenuItem {Text="HDD Spindown öffnen",Bold=true,Invoke=ShowFromTray},TrayMenuItem.Line()};
   if(!serviceReady) items.Add(new TrayMenuItem {Text="Dienst nicht bereit",Enabled=false});
   foreach(var d in Disks) {
    if(d.Action==DiskAction.None) { items.Add(new TrayMenuItem {Text=d.Title+"  –  nicht zugelassen",Enabled=false}); continue; }
    var item=d;
    string verb=d.Action==DiskAction.Sleep?"Schlafen":"Freigeben";
    string state=d.IsPaused?"pausiert":"verfügbar";
    items.Add(new TrayMenuItem {Text=d.Title+"  ("+state+")  –  "+verb,Enabled=d.ActionEnabled,Invoke=()=>OnDiskAction(item)});
   }
   items.Add(TrayMenuItem.Line());
   items.Add(new TrayMenuItem {Text="Alle freigeben",Enabled=!busy&&serviceReady&&Disks.Any(x=>x.IsPaused),Invoke=()=>ReleaseAll_Click(null,null)});
   items.Add(TrayMenuItem.Line());
   items.Add(new TrayMenuItem {Text="Beenden (pausierte HDDs bleiben pausiert)",Invoke=Quit});
   return items;
  }

  void UpdateTray(Response r) {
   int total=r?.Devices?.Length??0, paused=r?.Devices?.Count(v=>v.Operation!=null)??0;
   bool problem=r==null||!r.StoreHealthy||r.RecoveryPending;
   string text=r==null?"HDD Spindown · Dienst nicht erreichbar":"HDD Spindown · "+paused+" von "+total+" pausiert"+(r.RecoveryPending?" · Wiederherstellung offen":"");
   tray.SetTooltip(text);
   // Important faults are announced once per transition, never on normal sleep phases.
   if(problem&&!lastProblem&&!visible) tray.Notify(r==null?"HDD-Spindown-Dienst nicht erreichbar":"HDD Spindown: Wiederherstellung offen",r==null?"Pausieren und Freigeben sind gerade nicht möglich.":"Mindestens eine HDD ist nicht vollständig wiederhergestellt. Zum Ansehen auf das Symbol klicken.",true);
   lastProblem=problem;
  }

  // Caption buttons must follow the app theme, not only the system theme.
  void ApplyCaptionColors() {
   bool dark=Root.ActualTheme==ElementTheme.Dark;
   var tb=AppWindow.TitleBar;
   tb.ButtonForegroundColor=dark?Colors.White:Colors.Black;
   tb.ButtonInactiveForegroundColor=dark?ColorHelper.FromArgb(255,150,150,150):ColorHelper.FromArgb(255,120,120,120);
   tb.ButtonHoverBackgroundColor=dark?ColorHelper.FromArgb(25,255,255,255):ColorHelper.FromArgb(25,0,0,0);
   tb.ButtonHoverForegroundColor=dark?Colors.White:Colors.Black;
   tb.ButtonPressedBackgroundColor=dark?ColorHelper.FromArgb(40,255,255,255):ColorHelper.FromArgb(40,0,0,0);
   tb.ButtonPressedForegroundColor=dark?Colors.White:Colors.Black;
  }

  async void FirstActivated(object sender,WindowActivatedEventArgs e) {
   Activated-=FirstActivated;
   MarkShown();
   await Poll(Ops.Refresh);
   if(File.Exists(LegacyFile)&&serviceReady) {
    try { if(LegacyImport.ReadFile(LegacyFile).Length>0) ShowResult(InfoBarSeverity.Warning,"Ausstehende Zuordnungen aus Version 0.3","Die alte Version hat Laufwerksbuchstaben entfernt, die noch nicht wiederhergestellt sind. Über „…“ → „Zuordnungen aus Version 0.3 übernehmen“ stellt der Dienst sie wieder her.",false); } catch {}
   }
  }

  // ---------- service communication ----------
  public static bool Demo;
  static Task<Response> SendAsync(Request r,int timeout=3000) { return Task.Run(()=>Demo?DemoTransport.Send(r):ServiceClient.Send(r,timeout)); }

  async Task Poll(string op=null) {
   if(polling) return; polling=true;
   try {
    var r=await SendAsync(new Request {Op=op??Ops.List},op==Ops.Refresh?60000:2500);
    Apply(r);
   } catch(ServiceUnavailableException ex) { ShowServiceDown(ex.Message); }
   catch(Exception ex) { ShowServiceDown(ex.Message); }
   finally { polling=false; }
  }

  void ShowServiceDown(string detail) {
   uint pid; var state=ServiceClient.QueryState(out pid);
   serviceReady=false;
   switch(state) {
    case ServiceClient.ServiceState.NotInstalled: SetService(InfoBarSeverity.Error,"Dienst nicht installiert","HDD Spindown benötigt den Windows-Dienst. Bitte den Installer erneut ausführen."); break;
    case ServiceClient.ServiceState.Stopped: SetService(InfoBarSeverity.Warning,"Dienst gestoppt","Beim Beenden hat der Dienst alle Laufwerksbuchstaben wiederhergestellt. Zum Pausieren den Dienst „HDD Spindown“ starten (Dienste-Verwaltung) oder Windows neu starten."); break;
    case ServiceClient.ServiceState.StartPending: SetService(InfoBarSeverity.Informational,"Dienst startet","Offene Vorgänge werden abgeglichen …"); break;
    default: SetService(InfoBarSeverity.Error,"Dienst nicht erreichbar",detail); break;
   }
   TitleState.Text="";
   foreach(var d in Disks) d.Update(Snapshot(d),true,false,suspended);
   UpdateButtons();
   UpdateTray(null);
  }
  DeviceView Snapshot(DiskItem item) { return last?.Devices?.FirstOrDefault(v=>Key(v)==item.Key)??new DeviceView {Disk=new DiskInfo {Letters=item.Letters,Model=item.Model},Present=true}; }

  // Normal operation shows a quiet "Dienst aktiv" in the header; the bar is only for problems.
  void SetService(InfoBarSeverity severity,string title,string message) {
   bool ok=severity==InfoBarSeverity.Success;
   ServiceOk.Visibility=ok?Visibility.Visible:Visibility.Collapsed;
   ServiceBar.IsOpen=!ok;
   if(!ok) { ServiceBar.Severity=severity; ServiceBar.Title=title; ServiceBar.Message=message; }
  }

  static string Key(DeviceView v) { return v.Disk.DeviceId??("nr"+v.Disk.Number); }
  static string SortKey(DeviceView v) { var l=(v.Operation?.Letters?.FirstOrDefault())??v.Disk.Letters?.FirstOrDefault(); return (l??"~")+v.Disk.Number.ToString("000"); }

  void Apply(Response r) {
   last=r; suspended=r.Suspended;
   bool startup=r.Messages!=null&&r.Messages.Any(m=>m.Contains("Recovery-Werkzeug"));
   serviceReady=r.StoreHealthy&&!startup;
   if(!r.StoreHealthy) SetService(InfoBarSeverity.Error,"Zustandsspeicher nicht bestätigt",(r.Messages?.LastOrDefault())??"Der Dienst startet zur Wiederherstellung neu. Neue Schlafaktionen sind gesperrt.");
   else if(r.Suspended) SetService(InfoBarSeverity.Informational,"Energiesparmodus","Windows wechselt in den Energiesparmodus. Keine neuen Vorgänge.");
   else if(r.RecoveryPending) SetService(InfoBarSeverity.Warning,"Wiederherstellung offen","Mindestens eine HDD ist nicht vollständig wiederhergestellt. Details stehen an der jeweiligen HDD.");
   else SetService(InfoBarSeverity.Success,"Dienst aktiv","Die Sperren hält der Windows-Dienst. Dieses Fenster kann geschlossen werden.");
   TitleState.Text="· Dienst "+r.ServiceVersion;

   var views=(r.Devices??new DeviceView[0]).OrderBy(SortKey).ToList();
   var keys=new HashSet<string>(views.Select(Key));
   for(int i=Disks.Count-1;i>=0;i--) if(!keys.Contains(Disks[i].Key)) Disks.RemoveAt(i);
   for(int i=0;i<views.Count;i++) {
    var v=views[i]; string key=Key(v);
    var item=Disks.FirstOrDefault(d=>d.Key==key);
    if(item==null) { item=new DiskItem(key,OnDiskAction) {ShowFacts=showFacts}; Disks.Insert(Math.Min(i,Disks.Count),item); }
    else { int at=Disks.IndexOf(item); if(at!=i&&i<Disks.Count) Disks.Move(at,i); }
    item.Update(v,busy,serviceReady,suspended);
    NotifyExplorer(key,v);
   }
   EmptyText.Visibility=views.Count==0?Visibility.Visible:Visibility.Collapsed;
   int paused=views.Count(v=>v.Operation!=null);
   UpdateTray(r);
   Summary.Text=views.Count==0?"Keine SATA-HDDs":views.Count+(views.Count==1?" SATA-HDD":" SATA-HDDs")+"  ·  "+paused+" pausiert"+(views.Any(v=>v.Operation!=null&&v.Operation.Phase==Phase.RecoveryRequired)?"  ·  Wiederherstellung offen":"");
   UpdateButtons();
  }

  // The service runs in session 0; the shell of this user is told about letter changes here.
  void NotifyExplorer(string key,DeviceView v) {
   bool paused=v.Operation!=null&&v.Operation.LettersMissing!=null&&v.Operation.LettersMissing.Length>0;
   bool before; bool known=lastPaused.TryGetValue(key,out before); lastPaused[key]=paused;
   if(!known||before==paused) return;
   var letters=paused?v.Operation.LettersMissing:(v.Disk.Letters??new string[0]);
   foreach(var l in letters) try { SHChangeNotify(paused?0x80:0x100,0x0005|0x1000,l+@":\",IntPtr.Zero); } catch {}
  }
  [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern void SHChangeNotify(int code,uint flags,string item,IntPtr unused);

  void UpdateButtons() {
   RefreshButton.IsEnabled=!busy&&last!=null;
   ReleaseAllButton.IsEnabled=!busy&&serviceReady&&Disks.Any(d=>d.IsPaused);
  }

  void SetBusy(bool value) {
   busy=value; UpdateButtons();
   if(last!=null) foreach(var d in Disks) d.Update(Snapshot(d),busy,serviceReady,suspended);
  }

  void ShowResult(InfoBarSeverity severity,string title,string message,bool autoClose=true) {
   ResultBar.Severity=severity; ResultBar.Title=title; ResultBar.Message=message; ResultBar.IsOpen=true;
   // Actions started from the tray report their outcome there (progress notes excluded).
   if(!visible&&severity!=InfoBarSeverity.Informational) tray.Notify(title,message,severity==InfoBarSeverity.Warning,severity==InfoBarSeverity.Error);
  }

  // ---------- actions ----------
  async void OnDiskAction(DiskItem item) {
   if(busy||item.DeviceId==null) return;
   string letters=item.Letters.Length==0?item.Model:String.Join(", ",item.Letters.Select(l=>l+":"));
   bool sleep=item.Action==DiskAction.Sleep;
   SetBusy(true);
   ShowResult(InfoBarSeverity.Informational,sleep?letters+" wird gesperrt …":letters+" wird freigegeben …",sleep?"Volumes werden gesperrt, Laufwerksbuchstaben entfernt und Standby angefordert.":"Sperren werden gelöst und Laufwerksbuchstaben wiederhergestellt.");
   try {
    var r=await SendAsync(new Request {Op=sleep?Ops.Pause:Ops.Release,DeviceId=item.DeviceId,RequestId=sleep?Guid.NewGuid().ToString("D"):null},10000);
    Apply(r);
    if(r.Ok) ShowResult(InfoBarSeverity.Success,sleep?"Pausiert":"Freigegeben",(r.Messages?.FirstOrDefault())??"");
    else ShowResult(r.ErrorKind=="Policy"?InfoBarSeverity.Warning:InfoBarSeverity.Error,sleep?"Schlafen nicht möglich":"Freigabe nicht vollständig",r.Error+NextStep(r.ErrorKind,sleep));
   } catch(Exception ex) { ShowResult(InfoBarSeverity.Error,"Dienst nicht erreichbar",ex.Message); }
   finally { SetBusy(false); }
  }
  static string NextStep(string kind,bool sleep) {
   switch(kind) {
    case "Failed": return sleep?"\nNächster Schritt: Dateien, Explorer-Fenster und Programme auf dieser HDD schließen und erneut versuchen.":"\nNächster Schritt: Konflikt beheben (z. B. Buchstaben freigeben, HDD anschließen) und „Freigeben“ erneut wählen.";
    case "Denied": return "\nNur Konten der lokalen Administratorengruppe dürfen HDDs steuern.";
    case "Busy": return "";
    default: return "";
   }
  }

  async void Refresh_Click(object sender,RoutedEventArgs e) {
   SetBusy(true);
   try { await Poll(Ops.Refresh); } finally { SetBusy(false); }
  }

  async void ReleaseAll_Click(object sender,RoutedEventArgs e) {
   SetBusy(true);
   ShowResult(InfoBarSeverity.Informational,"Alle HDDs werden freigegeben …","Die HDDs werden nacheinander freigegeben, damit sie nicht gleichzeitig anlaufen.");
   try {
    var r=await SendAsync(new Request {Op=Ops.ReleaseAll},10000);
    Apply(r);
    ShowResult(r.Ok?InfoBarSeverity.Success:InfoBarSeverity.Error,r.Ok?"Alle HDDs freigegeben":"Freigabe nicht vollständig",String.Join("\n",r.Messages??new string[0]));
   } catch(Exception ex) { ShowResult(InfoBarSeverity.Error,"Dienst nicht erreichbar",ex.Message); }
   finally { SetBusy(false); }
  }

  async void Diagnostics_Click(object sender,RoutedEventArgs e) {
   string text;
   try { text=(await SendAsync(new Request {Op=Ops.Diagnostics})).Text; } catch(Exception ex) { text="Dienst nicht erreichbar: "+ex.Message; }
   var box=new TextBlock {Text=text,IsTextSelectionEnabled=true,FontFamily=new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),FontSize=12,TextWrapping=TextWrapping.Wrap};
   var dialog=new ContentDialog {XamlRoot=Root.XamlRoot,Title="Diagnose",Content=new ScrollViewer {Content=box,MaxHeight=420},PrimaryButtonText="Kopieren",CloseButtonText="Schließen",DefaultButton=ContentDialogButton.Close};
   if(await dialog.ShowAsync()==ContentDialogResult.Primary) { var p=new DataPackage(); p.SetText(text); Clipboard.SetContent(p); }
  }

  async void Legacy_Click(object sender,RoutedEventArgs e) {
   LegacyEntry[] entries;
   try { entries=LegacyImport.ReadFile(LegacyFile); } catch(Exception ex) { ShowResult(InfoBarSeverity.Error,"Altes Journal nicht lesbar",ex.Message); return; }
   if(entries.Length==0) { ShowResult(InfoBarSeverity.Informational,"Nichts zu übernehmen","Im Journal der Version 0.3 stehen keine ausstehenden Zuordnungen."); return; }
   var dialog=new ContentDialog {XamlRoot=Root.XamlRoot,Title="Zuordnungen aus Version 0.3 übernehmen",
    Content="Folgende Laufwerksbuchstaben wurden von der alten Version entfernt und werden wiederhergestellt, sofern sie frei sind:\n\n"+String.Join("\n",entries.Select(x=>"  "+x.Root))+"\n\nBelegte Buchstaben werden nicht überschrieben.",
    PrimaryButtonText="Wiederherstellen",CloseButtonText="Abbrechen",DefaultButton=ContentDialogButton.Primary};
   if(await dialog.ShowAsync()!=ContentDialogResult.Primary) return;
   SetBusy(true);
   try {
    var r=await SendAsync(new Request {Op=Ops.ImportLegacy,Legacy=entries},10000);
    Apply(r);
    if(r.Ok) { File.Move(LegacyFile,LegacyFile+".migriert-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")); LegacyMenuItem.IsEnabled=false; }
    ShowResult(r.Ok?InfoBarSeverity.Success:InfoBarSeverity.Warning,r.Ok?"Zuordnungen wiederhergestellt":"Nicht alle Zuordnungen wiederhergestellt",String.Join("\n",r.Messages??new string[0]));
   } catch(Exception ex) { ShowResult(InfoBarSeverity.Error,"Dienst nicht erreichbar",ex.Message); }
   finally { SetBusy(false); }
  }

  async void About_Click(object sender,RoutedEventArgs e) {
   var v=typeof(MainWindow).Assembly.GetName().Version;
   var dialog=new ContentDialog {XamlRoot=Root.XamlRoot,Title="HDD Spindown "+v.ToString(3),CloseButtonText="Schließen",
    Content="Sperrt interne SATA-Daten-HDDs, entfernt sie aus „Dieser PC“ und fordert Standby an. Ein Windows-Dienst hält die Sperren und stellt nach Absturz, Neustart oder Energiesparmodus die ursprüngliche Verfügbarkeit wieder her.\n\nKein Ersatz für ein Backup. Der Motorzustand wird nicht gemessen; eine bestimmte Lebensdauer wird nicht zugesagt.\n\nRecovery ohne Oberfläche: „HddSpindown.Recovery.exe“ im Installationsordner (als Administrator)."};
   await dialog.ShowAsync();
  }
 }

 // Minimum window size via WM_GETMINMAXINFO (WinUI 3 has no built-in minimum).
 static class MinSize {
  delegate IntPtr SubclassProc(IntPtr hwnd,uint msg,IntPtr w,IntPtr l,UIntPtr id,UIntPtr data);
  [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd,SubclassProc proc,UIntPtr id,UIntPtr data);
  [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
  [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
  static SubclassProc keep; static int minW,minH;
  public static void Attach(IntPtr hwnd,int w,int h) {
   minW=w; minH=h;
   keep=(hWnd,msg,wp,lp,id,data)=> {
    if(msg==0x0024) { double s=GetDpiForWindow(hWnd)/96.0; Marshal.WriteInt32(lp,24,(int)(minW*s)); Marshal.WriteInt32(lp,28,(int)(minH*s)); }
    return DefSubclassProc(hWnd,msg,wp,lp);
   };
   SetWindowSubclass(hwnd,keep,new UIntPtr(1),UIntPtr.Zero);
  }
 }
}
