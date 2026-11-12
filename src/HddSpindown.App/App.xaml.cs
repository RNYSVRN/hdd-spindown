using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;

namespace HddSpindown.App {
 public partial class App : Application {
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string title);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd,int cmd);
  static Mutex single;
  MainWindow window;
  public App() {
   InitializeComponent();
   // Follows the Windows app mode; --light/--dark only for visual checks of the preview.
   var args=Environment.GetCommandLineArgs();
   if(args.Contains("--light")) RequestedTheme=ApplicationTheme.Light;
   else if(args.Contains("--dark")) RequestedTheme=ApplicationTheme.Dark;
  }
  protected override void OnLaunched(LaunchActivatedEventArgs args) {
   MainWindow.Demo=Environment.GetCommandLineArgs().Any(a=>a=="--demo");
   bool owner;
   single=new Mutex(true,MainWindow.Demo?@"Local\HddSpindown.App.Demo":@"Local\HddSpindown.App.v1",out owner);
   if(!owner) {
    IntPtr existing=FindWindow(null,MainWindow.WindowTitle);
    if(existing!=IntPtr.Zero) { ShowWindow(existing,9); SetForegroundWindow(existing); }
    Exit(); return;
   }
   window=new MainWindow();
   window.Activate();
  }
 }
}
