using Anfeta.UI.Services.Search;
using Windows.Storage;
static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
var clock=new DateTime(2026,12,31,23,58,0);
var task=QuickTaskParser.Parse("Revisar #john pasado mañana p4",clock);
Check(task.Title=="Revisar"&&task.Priority=="P4"&&task.Tag=="#john"&&task.ScheduledDate=="2027-01-02","Etiquetas, p4, pasado mañana y año nuevo");
Check(QuickTaskParser.Parse("Llamar en cinco minutos p1",clock).ScheduledDate=="2027-01-01 00:03","Minutos cruzan medianoche");
Check(QuickTaskParser.Parse("Llamar mañana a las 14:30 alta",clock).ScheduledDate=="2027-01-01 14:30","Hora explícita y fecha relativa");
Check(QuickTaskParser.Parse("Revisar alta",clock).Priority=="P2","Alta debe ser P2");
Check(QuickTaskParser.Parse("Revisar baja",clock).Priority=="P4","Baja debe ser P4");
var url="https://example.com/media/13/10?x=1#john";
Check(QuickTaskParser.Parse("Abrir "+url+" p2",clock).Title=="Abrir "+url,"El parser no altera rutas ni fragmentos de URLs");
Check(QuickTaskParser.Parse("Escribir a persona@example.com",clock).Tag=="","Correo no es etiqueta");
try{QuickTaskParser.Parse("Llamar hoy 25:70",clock);throw new Exception("Hora inválida aceptada");}catch(FormatException){}
var values=ApplicationData.Current.LocalSettings.Values;
values[PendingTasksService.LS_PendingTasks]="corrupt-json";
try{PendingTasksService.AddTask(task);throw new Exception("Datos corruptos reemplazados");}catch(InvalidOperationException){}
Check((string?)values[PendingTasksService.LS_PendingTasks]=="corrupt-json","Conservar original corrupto");
values[PendingTasksService.LS_PendingTasks]="[]";values.ThrowOnWrite=true;
try{PendingTasksService.AddTask(task);throw new Exception("Guardado fallido declarado correcto");}catch(InvalidOperationException){}
values.ThrowOnWrite=false;PendingTasksService.TasksChanged+=(_,_)=>throw new Exception("Vista fallida");PendingTasksService.AddTask(task);
Check(PendingTasksService.LoadTasks().Count==1,"Aviso fallido no invalida el guardado confirmado");
Console.WriteLine("OK captura Desktop: prioridad, persona, fechas/horas, medianoche, año nuevo, errores de persistencia y datos originales conservados.");
namespace Anfeta.UI.Models.Search { public class SavedSearch {public string Title{get;set;}="";public string ScheduledDate{get;set;}="";public string Query{get;set;}="";public string Tag{get;set;}="";public string Priority{get;set;}="";public bool IsPendingTask{get;set;}} }
namespace Windows.Storage {
 public class TestValues {private readonly Dictionary<string,object?> store=new();public bool ThrowOnWrite{get;set;}public object? this[string key]{get=>store.TryGetValue(key,out var value)?value:null;set{if(ThrowOnWrite)throw new IOException("Sin espacio");store[key]=value;}}}
 public class ApplicationData {public static ApplicationData Current{get;}=new();public TestSettings LocalSettings{get;}=new();}
 public class TestSettings {public TestValues Values{get;}=new();}
}
