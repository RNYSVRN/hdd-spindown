using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HddSpindown.App {
 public sealed class TrayMenuItem {
  public string Text; public bool Enabled=true; public bool Separator; public bool Bold; public Action Invoke;
  public static TrayMenuItem Line() { return new TrayMenuItem {Separator=true}; }
 }

 // Native notification area icon without extra packages. A hidden top-level
 // window receives the icon callbacks, "TaskbarCreated" (Explorer restart) and the
 // activation request of a second app instance.
 public sealed class TrayIcon : IDisposable {
  public const string WindowClass="HddSpindown.Tray.v1";
  public const int WM_APP_SHOW=0x8000+2;
  const int WM_APP_TRAY=0x8000+1;
  const int WM_LBUTTONUP=0x0202, WM_LBUTTONDBLCLK=0x0203, WM_RBUTTONUP=0x0205, WM_CONTEXTMENU=0x007B, WM_NULL=0;
  const int NIM_ADD=0, NIM_MODIFY=1, NIM_DELETE=2;
  const int NIF_MESSAGE=1, NIF_ICON=2, NIF_TIP=4, NIF_INFO=0x10, NIF_SHOWTIP=0x80;
  const int NIIF_INFO=1, NIIF_WARNING=2, NIIF_ERROR=3, NIIF_NOSOUND=0x10;
  const uint MF_STRING=0, MF_GRAYED=1, MF_SEPARATOR=0x800, TPM_RETURNCMD=0x100, TPM_RIGHTBUTTON=2, TPM_BOTTOMALIGN=0x20;

  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
  struct NOTIFYICONDATA {
   public int cbSize; public IntPtr hWnd; public int uID; public int uFlags; public int uCallbackMessage; public IntPtr hIcon;
   [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string szTip;
   public int dwState; public int dwStateMask;
   [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string szInfo;
   public int uVersion;
   [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string szInfoTitle;
   public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
  }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
  struct WNDCLASSEX { public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm; }
  [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
  delegate IntPtr WndProc(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);

  [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg,ref NOTIFYICONDATA data);
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateWindowEx(int ex,string cls,string name,int style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr inst,IntPtr param);
  [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
  [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool AppendMenu(IntPtr m,uint flags,UIntPtr id,string text);
  [DllImport("user32.dll")] static extern bool SetMenuDefaultItem(IntPtr m,uint item,uint byPos);
  [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr m,uint flags,int x,int y,IntPtr h,IntPtr p);
  [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
  [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,int m,IntPtr w,IntPtr l);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr LoadImage(IntPtr inst,string name,uint type,int cx,int cy,uint load);
  [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
  // Undocumented but stable since Windows 10 1903: lets native popup menus follow dark mode.
  [DllImport("uxtheme.dll",EntryPoint="#135")] static extern int SetPreferredAppMode(int mode);
  [DllImport("uxtheme.dll",EntryPoint="#136")] static extern void FlushMenuThemes();

  readonly WndProc proc; readonly uint taskbarCreated;
  IntPtr hwnd, icon; string tip="HDD Spindown"; bool added;
  public Action Activate; public Func<IList<TrayMenuItem>> BuildMenu;

  public TrayIcon(string iconPath) {
   proc=Proc; taskbarCreated=RegisterWindowMessage("TaskbarCreated");
   try { SetPreferredAppMode(1); FlushMenuThemes(); } catch {}
   var wc=new WNDCLASSEX {cbSize=Marshal.SizeOf(typeof(WNDCLASSEX)),lpfnWndProc=proc,hInstance=GetModuleHandle(null),lpszClassName=WindowClass};
   if(RegisterClassEx(ref wc)==0) App.Trace("Tray: RegisterClassEx Fehler "+Marshal.GetLastWin32Error());
   hwnd=CreateWindowEx(0,WindowClass,"HDD Spindown Tray",0,0,0,0,0,IntPtr.Zero,IntPtr.Zero,wc.hInstance,IntPtr.Zero);
   if(hwnd==IntPtr.Zero) App.Trace("Tray: CreateWindowEx Fehler "+Marshal.GetLastWin32Error());
   int size=GetSystemMetrics(49); // SM_CXSMICON
   icon=LoadImage(IntPtr.Zero,iconPath,1,size,size,0x10); // IMAGE_ICON, LR_LOADFROMFILE
   Add();
  }
  NOTIFYICONDATA Data(int flags) {
   return new NOTIFYICONDATA {cbSize=Marshal.SizeOf(typeof(NOTIFYICONDATA)),hWnd=hwnd,uID=1,uFlags=flags,uCallbackMessage=WM_APP_TRAY,hIcon=icon,szTip=tip,szInfo="",szInfoTitle=""};
  }
  void Add() { var d=Data(NIF_MESSAGE|NIF_ICON|NIF_TIP|NIF_SHOWTIP); added=Shell_NotifyIcon(NIM_ADD,ref d); if(!added) App.Trace("Tray: Shell_NotifyIcon ADD fehlgeschlagen, icon="+icon); }
  public void SetTooltip(string text) {
   tip=text.Length>120?text.Substring(0,120):text;
   if(!added) return; var d=Data(NIF_TIP|NIF_SHOWTIP); Shell_NotifyIcon(NIM_MODIFY,ref d);
  }
  // Shown by Windows 11 as a notification.
  public void Notify(string title,string text,bool warning=false,bool error=false) {
   if(!added) return;
   var d=Data(NIF_INFO); d.szInfoTitle=Trim(title,63); d.szInfo=Trim(text,255);
   d.dwInfoFlags=(error?NIIF_ERROR:warning?NIIF_WARNING:NIIF_INFO)|(error||warning?0:NIIF_NOSOUND);
   Shell_NotifyIcon(NIM_MODIFY,ref d);
  }
  static string Trim(string s,int n) { s=s??""; return s.Length>n?s.Substring(0,n-1)+"…":s; }

  void ShowMenu() {
   var items=BuildMenu?.Invoke(); if(items==null||items.Count==0) return;
   IntPtr menu=CreatePopupMenu();
   try {
    for(int i=0;i<items.Count;i++) {
     var it=items[i];
     if(it.Separator) AppendMenu(menu,MF_SEPARATOR,UIntPtr.Zero,null);
     else { AppendMenu(menu,MF_STRING|(it.Enabled?0:MF_GRAYED),new UIntPtr((uint)(i+1)),it.Text); if(it.Bold) SetMenuDefaultItem(menu,(uint)(i+1),0); }
    }
    POINT p; GetCursorPos(out p);
    SetForegroundWindow(hwnd); // Required so the menu closes when clicking elsewhere.
    int cmd=TrackPopupMenuEx(menu,TPM_RETURNCMD|TPM_RIGHTBUTTON|TPM_BOTTOMALIGN,p.X,p.Y,hwnd,IntPtr.Zero);
    PostMessage(hwnd,WM_NULL,IntPtr.Zero,IntPtr.Zero);
    if(cmd>0&&cmd<=items.Count&&items[cmd-1].Enabled) items[cmd-1].Invoke?.Invoke();
   } finally { DestroyMenu(menu); }
  }

  IntPtr Proc(IntPtr h,uint msg,IntPtr w,IntPtr l) {
   if(msg==WM_APP_TRAY) {
    int ev=(int)(l.ToInt64()&0xFFFF);
    if(ev==WM_LBUTTONUP||ev==WM_LBUTTONDBLCLK) Activate?.Invoke();
    else if(ev==WM_RBUTTONUP||ev==WM_CONTEXTMENU) ShowMenu();
    return IntPtr.Zero;
   }
   if(msg==WM_APP_SHOW) { Activate?.Invoke(); return IntPtr.Zero; }
   if(msg==taskbarCreated) { Add(); return IntPtr.Zero; } // Explorer restarted.
   return DefWindowProc(h,msg,w,l);
  }

  public void Dispose() {
   if(added) { var d=Data(0); Shell_NotifyIcon(NIM_DELETE,ref d); added=false; }
   if(icon!=IntPtr.Zero) { DestroyIcon(icon); icon=IntPtr.Zero; }
   if(hwnd!=IntPtr.Zero) { DestroyWindow(hwnd); hwnd=IntPtr.Zero; }
  }
 }
}
