using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HddSpindown.Core;

namespace HddSpindown.App {
 // Preview mode (--demo): simulated HDDs, no service, no hardware access.
 static class DemoTransport {
  sealed class Disk { public DiskInfo Info; public Status Op; public string Block; }
  static readonly object gate=new object();
  static readonly List<Disk> disks=new List<Disk>();
  static DemoTransport() {
   void Add(int n,string model,string serial,long cap,string[] letters,bool ok=true,string reason=null) {
    disks.Add(new Disk {Info=new DiskInfo {DeviceId="SATA:"+model+":"+serial,Number=n,Model=model,Serial=serial,Firmware="SN03",Capacity=cap,Letters=letters,Eligible=ok,Reason=reason}});
   }
   Add(0,"ST18000NM000J-2TV103","ZR5000A1",18000000000000,new[]{"D"});
   Add(1,"ST18000NM000J-2TV103","ZR5000B2",18000000000000,new[]{"E"});
   Add(2,"ST6000VN0033-2EE110","ZAD0C3",6000000000000,new[]{"F","G"});
   Add(3,"WDC WD80EFZZ-68BTXN0","VK0D4",8000000000000,new[]{"H"});
   Add(4,"ST4000DM004-2CV104","ZFN0E5",4000000000000,new[]{"I"},false,L.Tok("BitLocker-/Geräteverschlüsselung aktiv – in 1.0 nicht unterstützt."));
   Add(5,"TOSHIBA MG08ACA16TE","Y0F6",16000000000000,new[]{"J"});
   disks[1].Op=new Status {RequestId=Guid.NewGuid().ToString("D"),DeviceId=disks[1].Info.DeviceId,Letters=new[]{"E"},LettersMissing=new[]{"E"},Phase=Phase.Paused,Access=AccessState.Locked,Hardware=HardwareState.StandbyRequested,StandbyUtc=DateTime.UtcNow.AddMinutes(-42).ToString("O")};
   disks[3].Block=L.Tok("Mindestlaufzeit nach Freigabe noch nicht erreicht. Erneut möglich in {0} min.",18);
  }
  public static Response Send(Request r) {
   if(r.Op==Ops.Pause||r.Op==Ops.Release||r.Op==Ops.ReleaseAll||r.Op==Ops.Refresh) Thread.Sleep(r.Op==Ops.Refresh?400:900);
   using(L.Use(L.Parse(r.Language)))
   lock(gate) {
    var messages=new List<string>(); bool ok=true; string error=null, kind=null;
    var d=disks.FirstOrDefault(x=>x.Info.DeviceId==r.DeviceId);
    if(r.Op==Ops.Pause&&d!=null) {
     if(d.Info.Letters.Contains("F")) { ok=false; kind="Failed"; error=L.T("Sperren – Dateien oder Programme sind möglicherweise noch geöffnet")+" (Windows 5). "+L.T("Keine Sperre behalten; Laufwerksbuchstaben sind wiederhergestellt."); }
     else { d.Op=new Status {RequestId=r.RequestId,DeviceId=d.Info.DeviceId,Letters=d.Info.Letters,LettersMissing=d.Info.Letters,Phase=Phase.Paused,Access=AccessState.Locked,Hardware=HardwareState.StandbyRequested,StandbyUtc=DateTime.UtcNow.ToString("O")}; messages.Add(L.F("{0} gesperrt und ausgeblendet · Standby angefordert.",String.Join(", ",d.Info.Letters.Select(l=>l+":")))); }
    }
    if(r.Op==Ops.Release&&d!=null) { d.Op=null; d.Block=L.Tok("Mindestlaufzeit nach Freigabe noch nicht erreicht. Erneut möglich in {0} min.",30); messages.Add(L.F("{0} wieder verfügbar.",String.Join(", ",d.Info.Letters.Select(l=>l+":")))); }
    if(r.Op==Ops.ReleaseAll) foreach(var x in disks.Where(x=>x.Op!=null)) { messages.Add(L.F("{0} wieder verfügbar.",String.Join(", ",x.Info.Letters.Select(l=>l+":")))); x.Op=null; }
    return new Response {Ok=ok,Error=error,ErrorKind=kind,ServiceVersion="1.0 · "+L.T("Vorschau"),StoreHealthy=true,Messages=messages.ToArray(),
     Text=L.T("Vorschau – keine Hardwareaktionen."),LastInventoryUtc=DateTime.UtcNow.ToString("O"),
     Devices=disks.Select(x=>new DeviceView {Disk=x.Info,Operation=x.Op,PolicyBlock=x.Op==null&&x.Info.Eligible?x.Block:null,Present=true}).ToArray()};
   }
  }
 }
}
