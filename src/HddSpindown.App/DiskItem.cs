using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using HddSpindown.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HddSpindown.App {
 public enum DiskAction { None, Sleep, Release }

 // View model for one HDD card. Updated in place to avoid flicker on refresh.
 // Texts come from the language catalog; service texts may contain tokens (L.Expand).
 public sealed class DiskItem : INotifyPropertyChanged {
  readonly Action<DiskItem> invoke;
  public DiskItem(string key,Action<DiskItem> onInvoke) { Key=key; invoke=onInvoke; }
  public event PropertyChangedEventHandler PropertyChanged;
  public string Key {get;private set;}
  public string DeviceId {get;private set;}
  public DiskAction Action {get;private set;}
  public string[] Letters {get;private set;}
  public bool IsPaused {get;private set;}

  string title,model,modelTip,stateText,access,explorer,hardware,hardwareTip,actionText,actionTip,actionGlyph,note,labelAccess;
  Brush stateBrush,iconBrush,noteBrush; Style actionStyle; bool actionEnabled,showFacts=true;
  void Set<T>(ref T field,T value,[CallerMemberName] string name=null) {
   if(Equals(field,value)) return; field=value; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
   if(name==nameof(Note)) PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(HasNote)));
   if(name==nameof(ActionText)||name==nameof(Title)) PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(ActionAutomationName)));
  }
  public string Title { get=>title; private set=>Set(ref title,value); }
  public string Model { get=>model; private set=>Set(ref model,value); }
  public string ModelTip { get=>modelTip; private set=>Set(ref modelTip,value); }
  public string StateText { get=>stateText; private set=>Set(ref stateText,value); }
  public string LabelAccess { get=>labelAccess; private set=>Set(ref labelAccess,value); }
  public string AccessText { get=>access; private set=>Set(ref access,value); }
  public string ExplorerText { get=>explorer; private set=>Set(ref explorer,value); }
  public string HardwareText { get=>hardware; private set=>Set(ref hardware,value); }
  public string HardwareTip { get=>hardwareTip; private set=>Set(ref hardwareTip,value); }
  public string ActionText { get=>actionText; private set=>Set(ref actionText,value); }
  public string ActionTip { get=>actionTip; private set=>Set(ref actionTip,value); }
  public string ActionGlyph { get=>actionGlyph; private set=>Set(ref actionGlyph,value); }
  public string Note { get=>note; private set=>Set(ref note,value); }
  public bool HasNote => !String.IsNullOrEmpty(note);
  public string ActionAutomationName => actionText+" "+(title??"");
  public Brush StateBrush { get=>stateBrush; private set=>Set(ref stateBrush,value); }
  public Brush IconBrush { get=>iconBrush; private set=>Set(ref iconBrush,value); }
  public Brush NoteBrush { get=>noteBrush; private set=>Set(ref noteBrush,value); }
  public Style ActionStyle { get=>actionStyle; private set=>Set(ref actionStyle,value); }
  public bool ActionEnabled { get=>actionEnabled; private set=>Set(ref actionEnabled,value); }
  public bool ShowFacts { get=>showFacts; set=>Set(ref showFacts,value); }
  public void Invoke() { invoke?.Invoke(this); }

  static Brush Res(string key) { return (Brush)Application.Current.Resources[key]; }
  static Style StyleRes(string key) { return (Style)Application.Current.Resources[key]; }
  static string Local(string utc) {
   DateTime t; if(utc!=null&&DateTime.TryParse(utc,null,System.Globalization.DateTimeStyles.RoundtripKind,out t)) {
    var local=t.ToLocalTime(); return local.Date==DateTime.Today?local.ToString("HH:mm"):local.ToString(L.Current==Lang.En?"MM/dd HH:mm":"dd.MM. HH:mm");
   }
   return null;
  }

  public void Update(DeviceView v,bool busy,bool serviceReady,bool suspended) {
   var d=v.Disk; var op=v.Operation;
   DeviceId=d.DeviceId; IsPaused=op!=null;
   LabelAccess=L.T("Zugriff");
   Letters=(op!=null&&op.Letters!=null&&op.Letters.Length>0?op.Letters:d.Letters)??new string[0];
   string letters=Letters.Length==0?L.T("Ohne Laufwerksbuchstaben"):String.Join("  ",Letters.Select(l=>l+":"));
   string size=d.Capacity>0?(d.Capacity/1e12).ToString(d.Capacity>=1e13?"0":"0.#")+" TB":null;
   Title=size==null?letters:letters+"  ·  "+size;
   Model=L.Expand(d.Model)??L.T("Unbekanntes Modell");
   ModelTip=Model+"\n"+L.T("Seriennummer")+": "+(String.IsNullOrEmpty(d.Serial)?"–":d.Serial)+"\nFirmware: "+(d.Firmware??"–")+(v.Present?"\n"+L.F("Datenträger {0}",d.Number):"");

   var success=Res("SystemFillColorSuccessBrush"); var caution=Res("SystemFillColorCautionBrush");
   var critical=Res("SystemFillColorCriticalBrush"); var neutral=Res("TextFillColorTertiaryBrush");
   var secondary=Res("TextFillColorSecondaryBrush"); var accent=Res("AccentTextFillColorPrimaryBrush");

   string noteText=null; Brush noteColor=secondary;
   if(op==null) {
    AccessText=L.T("Freigegeben"); ExplorerText=d.Letters!=null&&d.Letters.Length>0?L.T("Sichtbar"):L.T("Keine Buchstaben");
    HardwareText=L.T("Nicht überwacht"); HardwareTip=L.T("Der Motorzustand wird nicht abgefragt, um die HDD nicht aufzuwecken.");
    if(d.Eligible) {
     StateText=v.PolicyBlock==null?L.T("Verfügbar"):L.T("Verfügbar · Schonzeit läuft"); StateBrush=v.PolicyBlock==null?success:caution; IconBrush=secondary;
     if(v.PolicyBlock!=null) { noteText=L.Expand(v.PolicyBlock); noteColor=caution; }
     Action=DiskAction.Sleep;
    } else {
     StateText=L.T("Nicht zugelassen"); StateBrush=neutral; IconBrush=neutral; noteText=L.Expand(d.Reason); Action=DiskAction.None;
    }
   } else {
    bool recovery=op.Phase==Phase.RecoveryRequired;
    AccessText=op.Access==AccessState.Locked?L.T("Volumes gesperrt"):op.Access==AccessState.Released?L.T("Freigegeben"):L.T("Sperrstatus unklar");
    bool missing=op.LettersMissing!=null&&op.LettersMissing.Length>0;
    ExplorerText=recovery?(missing?L.T("Wiederherstellung offen"):L.T("Prüfung offen")):missing?L.T("Ausgeblendet"):L.T("Sichtbar");
    string when=Local(op.StandbyUtc);
    HardwareText=op.Hardware==HardwareState.StandbyRequested?L.T("Standby angefordert"):L.T("Unbekannt");
    HardwareTip=op.Hardware==HardwareState.StandbyRequested?L.T("Die HDD hat den Standby-Befehl bestätigt. Ob der Motor steht, wird nicht gemessen."):L.T("Nach Neustart oder Energiesparmodus ist der Motorzustand unbekannt.");
    if(!v.Present) { StateText=L.T("Nicht angeschlossen · Vorgang offen"); StateBrush=critical; IconBrush=neutral; noteText=L.T("Die HDD fehlt. Nach dem Anschließen versucht der Dienst die Wiederherstellung automatisch."); noteColor=critical; }
    else if(recovery) { StateText=L.T("Wiederherstellung offen"); StateBrush=critical; IconBrush=critical; noteText=L.Expand(FirstLine(op.Error))??L.T("Bitte „Freigeben“ erneut versuchen."); noteColor=critical; }
    else { StateText=when!=null?L.F("Pausiert seit {0}",when):L.T("Pausiert"); StateBrush=accent; IconBrush=accent; }
    Action=DiskAction.Release;
   }
   Note=noteText; NoteBrush=noteColor;
   if(Action==DiskAction.Release) { ActionText=L.T("Freigeben"); ActionGlyph=""; ActionStyle=StyleRes("DefaultButtonStyle"); ActionTip=L.T("Sperren lösen und dieselben Laufwerksbuchstaben wiederherstellen."); }
   else { ActionText=L.T("Schlafen"); ActionGlyph=""; ActionStyle=StyleRes("AccentButtonStyle"); ActionTip=Action==DiskAction.None?(L.Expand(d.Reason)??L.T("Nicht zugelassen.")):L.T("Volumes sperren, aus „Dieser PC“ entfernen und Standby anfordern. Dateien auf der HDD müssen geschlossen sein."); }
   ActionEnabled=serviceReady&&!busy&&Action!=DiskAction.None&&!(Action==DiskAction.Sleep&&(suspended||v.PolicyBlock!=null))&&!(Action==DiskAction.Release&&!v.Present);
  }
  static string FirstLine(string s) {
   if(String.IsNullOrEmpty(s)) return null;
   // Stored errors are full exception texts; show only the message part.
   int i=s.IndexOfAny(new[]{'\r','\n'}); string line=i<0?s:s.Substring(0,i);
   int colon=line.IndexOf(": "); if(colon>0&&line.Substring(0,colon).Contains("Exception")) line=line.Substring(colon+2);
   return line;
  }
 }
}
