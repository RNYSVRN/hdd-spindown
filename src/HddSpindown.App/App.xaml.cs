using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;

namespace HddSpindown.App {
 public partial class App : Application {
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string title);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,int msg,IntPtr w,IntPtr l);
  [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
  static Mutex single;
  MainWindow window;
  public static void Trace(string text) {
   try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"HDD-Spindown-App.log"),DateTime.Now.ToString("O")+" "+text+Environment.NewLine); } catch {}
  }
  public App() {
   InitializeComponent();
   UnhandledException+=(s,e)=>Trace("UnhandledException: "+e.Exception);
   AppDomain.CurrentDomain.UnhandledException+=(s,e)=>Trace("AppDomain: "+e.ExceptionObject);
   // Follows the Windows app mode; --light/--dark only for visual checks of the preview.
   var args=Environment.GetCommandLineArgs();
   // --lang de|en: language for this run only (not saved).
   int li=Array.IndexOf(args,"--lang");
   if(li>=0&&li+1<args.Length) { var l=HddSpindown.Core.L.Parse(args[li+1]); if(l.HasValue) HddSpindown.Core.L.Default=l.Value; }
   if(args.Contains("--light")) RequestedTheme=ApplicationTheme.Light;
   else if(args.Contains("--dark")) RequestedTheme=ApplicationTheme.Dark;
  }
  protected override void OnLaunched(LaunchActivatedEventArgs args) {
   MainWindow.Demo=Environment.GetCommandLineArgs().Any(a=>a=="--demo");
   bool owner;
   single=new Mutex(true,MainWindow.Demo?@"Local\HddSpindown.App.Demo":@"Local\HddSpindown.App.v1",out owner);
   if(!owner) {
    // Ask the running instance (possibly hidden in the tray) to show its window.
    IntPtr existing=FindWindow(TrayIcon.WindowClass,null);
    if(existing!=IntPtr.Zero) { AllowSetForegroundWindow(-1); PostMessage(existing,TrayIcon.WM_APP_SHOW,IntPtr.Zero,IntPtr.Zero); }
    Exit(); return;
   }
   window=new MainWindow();
   // --tray: autostart at logon, only the notification area icon.
   if(Environment.GetCommandLineArgs().Contains("--tray")) window.StartHidden();
   else window.Activate();
  }
 }
}
