using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using HddSpindown.Windows;

namespace HddSpindown.Service {
 // Bounded local log on the system SSD (5 x 1 MiB) plus Windows event log for
 // important failures. No file contents, no network.
 public static class Log {
  static readonly object gate=new object();
  const long MaxBytes=1024*1024; const int Keep=5;
  static string file;
  static bool eventLog;
  public static bool ConsoleEcho;
  public static Action<string> Sink; // Tests only.
  public static void Init() {
   try { Paths.EnsureProtectedDirectory(Paths.LogDir); file=Path.Combine(Paths.LogDir,"service.log"); } catch { file=null; }
   try { if(!EventLog.SourceExists(Paths.EventSource)) EventLog.CreateEventSource(Paths.EventSource,"Application"); eventLog=true; } catch { eventLog=false; }
  }
  public static void Info(string text) { Write("INFO",text,null); }
  public static void Warn(string text) { Write("WARN",text,EventLogEntryType.Warning); }
  public static void Error(string text,Exception ex=null) { Write("ERROR",ex==null?text:text+" | "+ex.GetType().Name+": "+ex.Message,EventLogEntryType.Error); if(ex!=null) Write("DETAIL",ex.ToString(),null); }
  static void Write(string level,string text,EventLogEntryType? entry) {
   string line=DateTime.UtcNow.ToString("O")+" "+level+" "+text.Replace("\r"," ").Replace("\n"," | ");
   if(ConsoleEcho) Console.WriteLine(line);
   var sink=Sink; if(sink!=null) try { sink(line); } catch {}
   lock(gate) {
    try {
     if(file!=null) {
      if(File.Exists(file)&&new FileInfo(file).Length>MaxBytes) Rotate();
      File.AppendAllText(file,line+Environment.NewLine,Encoding.UTF8);
     }
    } catch {}
   }
   if(entry.HasValue&&eventLog) try { EventLog.WriteEntry(Paths.EventSource,text.Length>30000?text.Substring(0,30000):text,entry.Value,entry==EventLogEntryType.Error?1001:1002); } catch {}
  }
  static void Rotate() {
   for(int i=Keep-1;i>=1;i--) { string a=file+"."+i, b=file+"."+(i+1); if(File.Exists(b)&&i==Keep-1) File.Delete(b); if(File.Exists(a)) File.Move(a,b,true); }
   File.Move(file,file+".1",true);
  }
 }
}
