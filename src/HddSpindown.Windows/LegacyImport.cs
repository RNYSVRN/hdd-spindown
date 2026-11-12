using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HddSpindown.Core;

namespace HddSpindown.Windows {
 // Restores letters recorded by the 0.3.x prototype (%LOCALAPPDATA%\HDD Spindown\mount-recovery.json).
 // Same rules as the core: never overwrite, never guess, verify after restoring.
 public static class LegacyImport {
  static readonly Regex RootPattern=new Regex(@"^[A-Z]:\\$",RegexOptions.IgnoreCase);
  static readonly Regex VolumePattern=new Regex(@"^\\\\\?\\Volume\{([0-9a-fA-F-]{36})\}\\$");
  public const int MaxEntries=64;

  public static LegacyEntry[] ReadFile(string path) {
   if(!File.Exists(path)) return new LegacyEntry[0];
   var info=new FileInfo(path); if(info.Length>64*1024) throw new InvalidOperationException("Altes Journal ist ungewöhnlich groß. Bitte manuell prüfen.");
   return Codec.Serializer().Deserialize<LegacyEntry[]>(File.ReadAllText(path))??new LegacyEntry[0];
  }
  public sealed class Result { public string Root; public bool Restored; public string Message; }

  public static Result[] Restore(IEnumerable<LegacyEntry> entries,IDeviceBackend backend) {
   var list=(entries??new LegacyEntry[0]).ToArray();
   if(list.Length>MaxEntries) throw new InvalidOperationException("Zu viele Einträge im alten Journal.");
   string system=Path.GetPathRoot(Environment.SystemDirectory);
   var results=new List<Result>();
   foreach(var e in list) {
    var r=new Result {Root=e==null?"?":e.Root};
    try {
     if(e==null||e.Root==null||e.Volume==null||!RootPattern.IsMatch(e.Root)) throw new InvalidOperationException("Ungültiger Eintrag.");
     var m=VolumePattern.Match(e.Volume); if(!m.Success) throw new InvalidOperationException("Ungültige Volume-Angabe.");
     if(String.Equals(e.Root,system,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Windows-Laufwerksbuchstabe wird nie verändert.");
     string letter=e.Root.Substring(0,1).ToUpperInvariant(); string guid=Guid.Parse(m.Groups[1].Value).ToString("D");
     string current=backend.LookupLetter(letter);
     if(current!=null&&!String.Equals(current,guid,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(letter+": ist inzwischen anderweitig belegt. Nichts überschrieben.");
     if(current==null) backend.RestoreLetter(letter,guid);
     if(!String.Equals(backend.LookupLetter(letter),guid,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Wiederherstellung von "+letter+": nicht bestätigt.");
     backend.NotifyExplorer(letter,true);
     r.Restored=true; r.Message=current==null?letter+": wiederhergestellt.":letter+": war bereits korrekt zugeordnet.";
    } catch(Exception ex) { r.Message=ex.Message; }
    results.Add(r);
   }
   return results.ToArray();
  }
 }
}
