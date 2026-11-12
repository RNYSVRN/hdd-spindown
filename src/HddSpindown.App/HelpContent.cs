using System;
using HddSpindown.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HddSpindown.App {
 // Built-in help, generated from the language catalog so it follows the selected language.
 static class HelpContent {
  static object Res(string key) { return Application.Current.Resources[key]; }
  static Brush Brush(string key) { return (Brush)Res(key); }

  static Border Card(string title,params UIElement[] content) {
   var panel=new StackPanel {Spacing=8};
   panel.Children.Add(new TextBlock {Text=L.T(title),Style=(Style)Res("SubtitleTextBlockStyle")});
   foreach(var c in content) panel.Children.Add(c);
   return new Border {Style=(Style)Res("CardBorderStyle"),Padding=new Thickness(20,16,20,16),Child=panel};
  }
  static TextBlock P(string text,bool muted=false) {
   var t=new TextBlock {Text=L.T(text),TextWrapping=TextWrapping.Wrap};
   if(muted) t.Foreground=Brush("TextFillColorSecondaryBrush");
   return t;
  }
  static TextBlock Bullet(string text) { return new TextBlock {Text="•  "+L.T(text),TextWrapping=TextWrapping.Wrap}; }
  // Two-column table: bold label, wrapped description; optional colored dot.
  static Grid Rows(params (string brush,string label,string text)[] rows) {
   var g=new Grid {ColumnSpacing=12,RowSpacing=8};
   bool dots=Array.Exists(rows,r=>r.brush!=null);
   if(dots) g.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(16)});
   g.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(dots?200:216)});
   g.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
   for(int i=0;i<rows.Length;i++) {
    g.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
    int col=0;
    if(dots) { var e=new Ellipse {Width=10,Height=10,Fill=Brush(rows[i].brush),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,5,0,0)}; Grid.SetRow(e,i); g.Children.Add(e); col=1; }
    var l=new TextBlock {Text=L.T(rows[i].label),Style=(Style)Res("BodyStrongTextBlockStyle"),TextWrapping=TextWrapping.Wrap}; Grid.SetRow(l,i); Grid.SetColumn(l,col); g.Children.Add(l);
    var t=new TextBlock {Text=L.T(rows[i].text),TextWrapping=TextWrapping.Wrap}; Grid.SetRow(t,i); Grid.SetColumn(t,col+1); g.Children.Add(t);
   }
   return g;
  }

  public static void Build(StackPanel panel,string recoveryCommands,Action copy,string version) {
   panel.Children.Clear();
   panel.Children.Add(Card("Was HDD Spindown macht",
    P("HDD Spindown legt interne SATA-Daten-HDDs gezielt schlafen, wenn du sie nicht brauchst. „Schlafen“ sperrt alle Volumes der HDD, entfernt ihre Laufwerksbuchstaben aus „Dieser PC“ und sendet den Befehl „Standby“. Solange die HDD pausiert ist, können normale Programme, Explorer und Indexdienste nicht auf sie zugreifen – sie wird also nicht ständig wieder aufgeweckt."),
    P("Die Sperren hält ein Windows-Dienst. Du kannst dieses Fenster schließen, dich abmelden oder die App beenden: Pausierte HDDs bleiben pausiert, bis du sie freigibst. „Freigeben“ löst die Sperren und gibt jeder HDD genau dieselben Laufwerksbuchstaben zurück.")));

   panel.Children.Add(Card("Die Anzeige einer HDD lesen",
    P("Der Punkt am Laufwerkssymbol und die farbige Zeile unter dem Modell zeigen den Gesamtzustand:",true),
    Rows(("SystemFillColorSuccessBrush","Verfügbar","HDD ist normal nutzbar und kann schlafen gelegt werden."),
         ("SystemFillColorCautionBrush","Schonzeit läuft","HDD ist nutzbar, darf aber gerade nicht erneut schlafen (siehe Schonregeln). Die Restzeit steht darunter."),
         ("AccentTextFillColorPrimaryBrush","Pausiert seit …","Volumes gesperrt, Buchstaben ausgeblendet, Standby angefordert."),
         ("SystemFillColorCriticalBrush","Wiederherstellung offen","Ein Buchstabe konnte nicht zurückgegeben werden (z. B. inzwischen belegt oder HDD fehlt). Der Grund steht an der Karte; nach Behebung „Freigeben“ erneut wählen."),
         ("TextFillColorTertiaryBrush","Nicht zugelassen","Die HDD erfüllt eine Voraussetzung nicht (siehe „Welche HDDs unterstützt werden“). Der Grund steht an der Karte.")),
    P("Rechts daneben stehen drei getrennte Angaben, weil sie unabhängig voneinander gelten:",true),
    Rows((null,"Zugriff","„Freigegeben“: Dateien sind erreichbar. „Volumes gesperrt“: der Dienst hält die Sperre. „Sperrstatus unklar“: ein Vorgang ist offen, aber die Sperre ist nicht nachweisbar (z. B. nach einem Dienstneustart) – dann wird wiederhergestellt."),
         (null,"Explorer","„Sichtbar“: Laufwerksbuchstaben vorhanden. „Ausgeblendet“: Buchstaben entfernt, die HDD fehlt in „Dieser PC“. „Wiederherstellung offen“: mindestens ein Buchstabe ist noch nicht zurück."),
         (null,"Hardware","„Nicht überwacht“: Im Normalbetrieb wird der Motor nicht abgefragt, damit die HDD nicht aufwacht. „Standby angefordert“: die HDD hat den Befehl bestätigt – ob der Motor wirklich steht, wird nicht gemessen. „Unbekannt“: nach Neustart oder Energiesparmodus ist der Zustand nicht bekannt."))));

   panel.Children.Add(Card("Bedienung",
    Rows((null,"Schlafen","Vorher alle Dateien, Explorer-Fenster und Programme auf dieser HDD schließen. Ist noch etwas geöffnet, wird nichts verändert und die Meldung nennt den nächsten Schritt – es wird nie etwas zwangsweise geschlossen."),
         (null,"Freigeben","Gibt die HDD sofort wieder frei. Beim nächsten Zugriff läuft sie an; das dauert einige Sekunden."),
         (null,"Alle freigeben","Gibt alle pausierten HDDs nacheinander mit einigen Sekunden Abstand frei, damit sie nicht gleichzeitig anlaufen."),
         (null,"Neu einlesen","Erkennt neu angeschlossene oder geänderte HDDs. Pausierte HDDs werden dabei nicht abgefragt."),
         (null,"Infobereich","Das X schließt nur das Fenster; das Symbol bleibt im Infobereich (ggf. unter dem Pfeil ^). Linksklick öffnet das Fenster, Rechtsklick bietet Schlafen/Freigeben je HDD. Bei der Anmeldung startet nur das Symbol (abschaltbar unter Einstellungen → Apps → Autostart)."),
         (null,"Sprache","Unter „…“ → „Sprache“ lässt sich zwischen Deutsch, Englisch und der Windows-Sprache wählen. Die Umstellung wirkt sofort."),
         (null,"Beenden","Beendet nur die Oberfläche. Pausierte HDDs bleiben pausiert, weil der Dienst die Sperren hält."))));

   panel.Children.Add(Card("Schonregeln",
    P("Jedes Anlaufen belastet Motor und Mechanik. Schnelle Wechsel zwischen Schlafen und Anlaufen schaden einer HDD mehr, als kurze Pausen nutzen. Deshalb gilt je HDD:"),
    Bullet("Nach einer Freigabe muss die HDD mindestens 30 Minuten laufen, bevor sie wieder schlafen darf („Schonzeit läuft“)."),
    Bullet("Höchstens 4 Standby-Anforderungen innerhalb von 24 Stunden."),
    Bullet("Freigeben ist immer sofort möglich."),
    P("Das sind vorsichtige Richtwerte, keine Herstellergarantie. Manche Hersteller sehen für ihre Modelle eigene Werte vor.",true)));

   panel.Children.Add(Card("Was in besonderen Situationen passiert",
    Rows((null,"Neustart / Herunterfahren","Alle Laufwerksbuchstaben werden wiederhergestellt. Nach dem Start sind alle HDDs verfügbar; ein früherer Schlafwunsch wird nicht automatisch wiederholt."),
         (null,"Absturz / Stromausfall","Der Dienst merkt sich jeden Schritt, bevor er ihn ausführt. Beim nächsten Start gibt er alle Buchstaben anhand dieser Aufzeichnung zurück – belegte Buchstaben werden nie überschrieben."),
         (null,"Energiesparmodus","Während des Übergangs sind keine neuen Aktionen möglich. Nach dem Aufwachen bleiben pausierte HDDs gesperrt, wenn die Sperre noch nachweisbar ist; der Hardwarezustand ist dann „Unbekannt“. Windows lässt beim Aufwachen oft alle HDDs kurz anlaufen."),
         (null,"HDD fehlt","Ihr Vorgang bleibt offen und wird automatisch abgeschlossen, sobald sie wieder angeschlossen ist. Andere HDDs bleiben bedienbar."),
         (null,"HDD wacht trotzdem auf","Programme mit direktem Gerätezugriff (z. B. SMART-Tools, Controller-Software) können eine pausierte HDD aufwecken. Die Sperre bleibt trotzdem bestehen; Dateien bleiben bis zur Freigabe unzugänglich."))));

   panel.Children.Add(Card("Welche HDDs unterstützt werden",
    P("Interne, direkt per SATA angeschlossene Festplatten mit GPT-Partitionstabelle und einfachen, fehlerfreien NTFS-Datenvolumes."),
    P("Nicht unterstützt: SSDs und NVMe (werden ausgeblendet), USB-Laufwerke, RAID und Storage Spaces, dynamische Datenträger, MBR-Datenträger, Datenträger mit Windows, Programmen oder Auslagerungsdatei, BitLocker-verschlüsselte Volumes und Volumes mit gesetztem Fehlerkennzeichen (dann zuerst mit „chkdsk“ prüfen).",true)));

   var code=new Border {Background=Brush("SubtleFillColorSecondaryBrush"),CornerRadius=new CornerRadius(4),Padding=new Thickness(12,10,12,10),
    Child=new TextBlock {Text=recoveryCommands,IsTextSelectionEnabled=true,FontFamily=new FontFamily("Cascadia Mono, Consolas"),FontSize=13,TextWrapping=TextWrapping.Wrap}};
   var copyContent=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};
   copyContent.Children.Add(new FontIcon {Glyph="",FontSize=14});
   copyContent.Children.Add(new TextBlock {Text=L.T("Befehle kopieren")});
   var copyButton=new Button {Content=copyContent};
   Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copyButton,L.T("Befehle kopieren"));
   copyButton.Click+=(s,e)=>copy();
   panel.Children.Add(Card("Notfall und Recovery",
    P("Fehlt ein Laufwerksbuchstabe dauerhaft oder reagiert die App nicht, hilft das Recovery-Werkzeug – auch ohne diese Oberfläche. Eingabeaufforderung als Administrator öffnen und eingeben:"),
    code,copyButton,
    P("„sc stop“ beendet den Dienst; dabei gibt er alle HDDs frei. „recover“ arbeitet nur bei gestopptem Dienst und nutzt dieselben Prüfungen: keine geratenen Zuordnungen, keine Ersatzbuchstaben, keine Reparaturen. Mit „sc start HddSpindown“ oder einem Neustart läuft der Dienst wieder.",true),
    P("Hinweise zur Fehlersuche: „…“ → „Diagnose anzeigen“ (mit Kopieren) sowie die Ereignisanzeige unter Windows-Protokolle → Anwendung, Quelle „HDD Spindown“.",true)));

   var footer=new TextBlock {Text="HDD Spindown "+version+" · "+L.T("Weitere Optionen des Recovery-Werkzeugs: „list“, „import-legacy“, „export“ (Diagnosebericht), „--lang en“."),Style=(Style)Res("CaptionTextBlockStyle"),Foreground=Brush("TextFillColorTertiaryBrush"),Margin=new Thickness(0,8,0,0),TextWrapping=TextWrapping.Wrap};
   panel.Children.Add(Card("Wichtig zu wissen",
    P("HDD Spindown ersetzt kein Backup und schützt nicht vor Stromausfall, mechanischen Defekten oder Schadsoftware mit Administratorrechten. Eine bestimmte Lebensdauer wird nicht zugesagt."),
    P("Steuern dürfen nur Konten der lokalen Administratorengruppe. Die App selbst läuft ohne erhöhte Rechte."),
    footer));
  }
 }
}
