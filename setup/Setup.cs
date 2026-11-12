// HDD Spindown Setup: language selection, then runs the embedded MSI in that language.
// Built with the .NET Framework compiler that ships with Windows (no extra runtime).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("HDD Spindown Setup")]
[assembly: AssemblyProduct("HDD Spindown")]
[assembly: AssemblyCompany("HDD Spindown")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion(SetupInfo.Version)]
[assembly: AssemblyFileVersion(SetupInfo.Version)]

static partial class SetupInfo { }

sealed class Theme {
 public bool Dark; public Color Back, Surface, Text, Muted, Border, Accent, OnAccent;
 [DllImport("dwmapi.dll")] static extern int DwmGetColorizationColor(out uint color,out bool opaque);
 public static Theme Read() {
  bool dark=false;
  try { dark=Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1))==0; } catch {}
  Color accent=dark?Color.FromArgb(96,205,255):Color.FromArgb(0,95,184);
  // AccentPalette: 8 RGBA entries (Light3, Light2, Light1, Base, Dark1, Dark2, Dark3, …).
  // Like WinUI accent buttons: Light2 in dark mode, Dark1 in light mode.
  try {
   var pal=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent","AccentPalette",null) as byte[];
   if(pal!=null&&pal.Length>=32) { int i=(dark?1:4)*4; accent=Color.FromArgb(pal[i],pal[i+1],pal[i+2]); }
   else { uint c; bool o; if(DwmGetColorizationColor(out c,out o)==0) accent=Color.FromArgb((int)((c>>16)&0xFF),(int)((c>>8)&0xFF),(int)(c&0xFF)); }
  } catch {}
  var t=dark?new Theme {Dark=true,Back=Color.FromArgb(32,32,32),Surface=Color.FromArgb(43,43,43),Text=Color.White,Muted=Color.FromArgb(200,200,200),Border=Color.FromArgb(60,60,60)}
            :new Theme {Back=Color.FromArgb(243,243,243),Surface=Color.White,Text=Color.FromArgb(27,27,27),Muted=Color.FromArgb(96,96,96),Border=Color.FromArgb(229,229,229)};
  t.Accent=accent;
  double lum=(0.299*accent.R+0.587*accent.G+0.114*accent.B)/255;
  t.OnAccent=lum>0.6?Color.Black:Color.White;
  return t;
 }
}

// Flat, rounded Windows 11 style button / option tile.
sealed class Tile : Control {
 public Theme T; public bool Selected, Primary; public string Sub;
 bool hover;
 public Tile() { SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true); Cursor=Cursors.Hand; TabStop=true; }
 protected override void OnMouseEnter(EventArgs e) { hover=true; Invalidate(); base.OnMouseEnter(e); }
 protected override void OnMouseLeave(EventArgs e) { hover=false; Invalidate(); base.OnMouseLeave(e); }
 protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
 protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
 protected override void OnKeyDown(KeyEventArgs e) { if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
 static GraphicsPath Round(RectangleF r,float rad) { var p=new GraphicsPath(); float d=rad*2; p.AddArc(r.X,r.Y,d,d,180,90); p.AddArc(r.Right-d,r.Y,d,d,270,90); p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); p.AddArc(r.X,r.Bottom-d,d,d,90,90); p.CloseFigure(); return p; }
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Parent.BackColor);
  float s=DeviceDpi/96f; var r=new RectangleF(1,1,Width-3,Height-3);
  Color fill=Primary?(Enabled?T.Accent:T.Border):(hover?ControlPaint.Light(T.Surface,T.Dark?0.15f:-0.03f):T.Surface);
  if(!T.Dark&&!Primary&&hover) fill=Color.FromArgb(249,249,249);
  if(T.Dark&&!Primary&&hover) fill=Color.FromArgb(50,50,50);
  using(var path=Round(r,6*s)) {
   using(var b=new SolidBrush(fill)) g.FillPath(b,path);
   using(var pen=new Pen(Selected?T.Accent:(Primary?fill:T.Border),Selected?2*s:1)) g.DrawPath(pen,path);
  }
  var fg=Primary?T.OnAccent:T.Text;
  if(Sub==null) TextRenderer.DrawText(g,Text,Font,Rectangle.Round(r),fg,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
  else {
   var top=new Rectangle((int)(16*s),(int)(10*s),Width-(int)(48*s),Height/2-(int)(6*s));
   using(var bold=new Font(Font,FontStyle.Bold)) TextRenderer.DrawText(g,Text,bold,top,fg,TextFormatFlags.Left|TextFormatFlags.Bottom);
   TextRenderer.DrawText(g,Sub,Font,new Rectangle(top.X,Height/2,top.Width,Height/2-(int)(8*s)),T.Muted,TextFormatFlags.Left|TextFormatFlags.Top);
   // radio indicator
   float d=16*s, x=Width-d-16*s, y=(Height-d)/2;
   using(var pen=new Pen(Selected?T.Accent:T.Muted,1.5f*s)) g.DrawEllipse(pen,x,y,d,d);
   if(Selected) using(var b=new SolidBrush(T.Accent)) g.FillEllipse(b,x+d*0.25f,y+d*0.25f,d*0.5f,d*0.5f);
  }
  if(Focused&&ShowFocusCues) using(var pen=new Pen(T.Text,1) {DashStyle=DashStyle.Dot}) using(var p=Round(new RectangleF(3,3,Width-7,Height-7),4*s)) g.DrawPath(pen,p);
 }
}

sealed class SetupForm : Form {
 [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
 readonly Theme t=Theme.Read();
 readonly Label title=new Label(), sub=new Label(), note=new Label();
 readonly Tile de=new Tile {Text="Deutsch"}, en=new Tile {Text="English"}, install=new Tile {Primary=true}, cancel=new Tile();
 public string Language;
 public SetupForm(string preselect) {
  Language=preselect;
  Text="HDD Spindown Setup"; FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false; MinimizeBox=false; StartPosition=FormStartPosition.CenterScreen;
  AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96,96); ClientSize=new Size(460,330);
  BackColor=t.Back; ForeColor=t.Text;
  Font=new Font(FontFamily.Families.Length>0&&IsInstalled("Segoe UI Variable Text")?"Segoe UI Variable Text":"Segoe UI",9.75f);
  try { Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch {}
  var logo=new PictureBox {Location=new Point(28,26),Size=new Size(40,40),SizeMode=PictureBoxSizeMode.Zoom};
  try { logo.Image=Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch {}
  title.SetBounds(80,24,360,28); title.Font=new Font(Font.FontFamily,15f,FontStyle.Bold); title.AutoSize=false;
  sub.SetBounds(81,52,360,20); sub.ForeColor=t.Muted; sub.AutoSize=false;
  foreach(var tile in new[]{de,en}) { tile.T=t; tile.Size=new Size(404,58); }
  de.Location=new Point(28,92); en.Location=new Point(28,158);
  de.Sub="Installer und App auf Deutsch"; en.Sub="Installer and app in English";
  de.Click+=delegate { Select("de"); }; en.Click+=delegate { Select("en"); };
  note.SetBounds(28,226,404,36); note.ForeColor=t.Muted; note.AutoSize=false;
  install.T=t; cancel.T=t;
  install.SetBounds(192,276,120,34); cancel.SetBounds(320,276,112,34);
  install.Click+=delegate { DialogResult=DialogResult.OK; Close(); };
  cancel.Click+=delegate { DialogResult=DialogResult.Cancel; Close(); };
  Controls.AddRange(new Control[]{logo,title,sub,de,en,note,install,cancel});
  foreach(Control c in Controls) if(!(c is Tile)) c.BackColor=t.Back;
  AcceptButton=null; KeyPreview=true;
  KeyDown+=(s,e)=> { if(e.KeyCode==Keys.Escape) { DialogResult=DialogResult.Cancel; Close(); } };
  Select(Language);
 }
 static bool IsInstalled(string family) { foreach(var f in FontFamily.Families) if(f.Name==family) return true; return false; }
 void Select(string lang) {
  Language=lang; bool deSel=lang=="de";
  de.Selected=deSel; en.Selected=!deSel;
  title.Text=deSel?"HDD Spindown installieren":"Install HDD Spindown";
  sub.Text=(deSel?"Version ":"Version ")+SetupInfo.Version.Substring(0,SetupInfo.Version.LastIndexOf('.'))+(deSel?" · Sprache wählen":" · Choose a language");
  note.Text=deSel?"Die Sprache lässt sich später in der App unter „…“ → „Sprache“ jederzeit ändern. Für die Installation sind Administratorrechte nötig.":"You can change the language later in the app under “…” → “Language”. Installation requires administrator rights.";
  install.Text=deSel?"Installieren":"Install"; cancel.Text=deSel?"Abbrechen":"Cancel";
  foreach(Control c in Controls) c.Invalidate();
 }
 protected override void OnHandleCreated(EventArgs e) {
  base.OnHandleCreated(e);
  try { int dark=t.Dark?1:0, round=2; DwmSetWindowAttribute(Handle,20,ref dark,4); DwmSetWindowAttribute(Handle,33,ref round,4); } catch {}
 }
 protected override void OnShown(EventArgs e) { base.OnShown(e); (Language=="de"?de:en).Focus(); }
}

static class Program {
 [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
 [STAThread]
 static int Main(string[] args) {
  try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch {}
  Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
  string lang=CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="de"?"de":"en";
  bool quiet=false, ask=true;
  for(int i=0;i<args.Length;i++) {
   string a=args[i].ToLowerInvariant().TrimStart('/','-');
   if((a=="lang"||a=="language")&&i+1<args.Length) { var v=args[++i].ToLowerInvariant(); if(v.StartsWith("de")) lang="de"; else if(v.StartsWith("en")) lang="en"; ask=false; }
   else if(a=="quiet"||a=="q"||a=="silent") { quiet=true; ask=false; }
  }
  if(ask) {
   using(var form=new SetupForm(lang)) { if(form.ShowDialog()!=DialogResult.OK) return 1602; lang=form.Language; }
  }
  string dir=Path.Combine(Path.GetTempPath(),"HDD-Spindown-Setup-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(dir);
  string msi=Path.Combine(dir,"HDD-Spindown.msi");
  try {
   using(var res=Assembly.GetExecutingAssembly().GetManifestResourceStream("HDD-Spindown.msi"))
   using(var file=File.Create(msi)) { if(res==null) throw new InvalidOperationException("MSI resource missing."); res.CopyTo(file); }
   string arguments="/i \""+msi+"\" APPLANGUAGE="+lang+(lang=="en"?" TRANSFORMS=:1033":"")+(quiet?" /qn":"");
   using(var p=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"msiexec.exe"),arguments) {UseShellExecute=false})) {
    p.WaitForExit();
    int code=p.ExitCode;
    if(code!=0&&code!=1602&&code!=3010&&!quiet)
     MessageBox.Show((lang=="de"?"Die Installation wurde mit Code ":"Setup finished with code ")+code+(lang=="de"?" beendet.":"."),"HDD Spindown Setup",MessageBoxButtons.OK,MessageBoxIcon.Warning);
    return code;
   }
  } catch(Exception ex) {
   if(!quiet) MessageBox.Show(ex.Message,"HDD Spindown Setup",MessageBoxButtons.OK,MessageBoxIcon.Error);
   return 1603;
  } finally {
   try { Directory.Delete(dir,true); } catch {}
  }
 }
}
