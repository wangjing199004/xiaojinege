using System;using System.IO;using System.Linq;using System.Text;using System.Reflection;using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;using Autodesk.AutoCAD.DatabaseServices;using Autodesk.AutoCAD.Geometry;
using CadApp=Autodesk.AutoCAD.ApplicationServices.Application;
public class IntegratedHostRegression {
 static BindingFlags Flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static StringBuilder log=new StringBuilder();
 static void Check(bool ok,string reason){if(!ok)throw new System.Exception(reason);}
 static object Call(Type t,string name,params object[] args){return t.GetMethod(name,Flags).Invoke(null,args);}
 [CommandMethod("BPI_TEST")] public void Run(){
 string root=@"F:\Codedemo\XJG批量打印_fix";string fixture=Path.Combine(root,"local-artifacts","integration-host");
 try{
 var loader=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="AcadBatchPlot.Loader").GetType("BatchPlotLoader.Commands",true);
 var entry=(Type)loader.GetField("_entry",Flags).GetValue(null);Check(entry!=null,"Loader did not initialize business DLL");
 Check(entry.Assembly.Location=="","Business DLL must be loaded from bytes");
 var firstName=entry.Assembly.GetName().Name;
 log.AppendLine("PASS first business runtime initialized: "+firstName);
 CheckAttributes(entry.Assembly);
 // Replacing an in-use business DLL must succeed; reject reload while the panel is operating.
 File.Copy(Path.Combine(fixture,"next.dll"),Path.Combine(fixture,"AcadBatchPlot.Runtime.dll"),true);
 var pending=entry.Assembly.GetType("ZwcadBatchPlot.PendingLibraryInput",true);
 pending.GetField("_executing",Flags).SetValue(null,true);
 bool blocked=false;try{Call(loader,"LoadCurrent");}catch(TargetInvocationException e){blocked=e.InnerException is InvalidOperationException;}
 Check(blocked,"Reload did not reject an active operation");
 Check((Type)loader.GetField("_entry",Flags).GetValue(null)==entry,"Rejected reload changed active entry");
 pending.GetField("_executing",Flags).SetValue(null,false);
 Call(loader,"LoadCurrent");
 var next=(Type)loader.GetField("_entry",Flags).GetValue(null);
 Check(next.Assembly.GetName().Name!=firstName,"WPF runtime identity was reused");

 log.AppendLine("PASS second business runtime initialized: "+next.Assembly.GetName().Name);
 CheckAttributes(next.Assembly);
 Check((bool)Call(next,"CanReload"),"Reload state did not return idle");
 var runtimePath=Path.Combine(fixture,"AcadBatchPlot.Runtime.dll");var good=File.ReadAllBytes(runtimePath);
 File.WriteAllBytes(runtimePath,new byte[]{1,2,3});bool rejected=false;
 try{Call(loader,"LoadCurrent");}catch(TargetInvocationException){rejected=true;}
 Check(rejected && (Type)loader.GetField("_entry",Flags).GetValue(null)==next,"Bad update damaged running plugin");
 File.WriteAllBytes(runtimePath,good);var currentCommands=next.GetField("_commands",Flags).GetValue(null);
 Call(loader,"LoadCurrent");Check(currentCommands!=null && currentCommands==next.GetField("_commands",Flags).GetValue(null),"Unchanged update restarted the business instance");
 log.AppendLine("PASS: invalid update keeps running version; unchanged file preserves business instance");
 Call(next,"Shutdown");
 log.AppendLine("PASS: byte-loaded business DLL is replaceable; busy operation blocks reload; distinct runtime identities; tag-based attribute writes work after reload");
 }catch(System.Exception ex){log.AppendLine("FAIL "+ex);}
 File.WriteAllText(Path.Combine(fixture,"integration-regression.txt"),log.ToString(),Encoding.UTF8);
 }
 static void CheckAttributes(Assembly assembly){
 var doc=CadApp.DocumentManager.MdiActiveDocument;ObjectId first,second,lockedLayer;string name="INTEGRATION_TEST_"+Guid.NewGuid().ToString("N");
 using(var tr=doc.Database.TransactionManager.StartTransaction()){
 var lt=(LayerTable)tr.GetObject(doc.Database.LayerTableId,OpenMode.ForWrite);var layer=new LayerTableRecord{Name=name};lockedLayer=lt.Add(layer);tr.AddNewlyCreatedDBObject(layer,true);
 var bt=(BlockTable)tr.GetObject(doc.Database.BlockTableId,OpenMode.ForWrite);var def=new BlockTableRecord{Name=name};bt.Add(def);tr.AddNewlyCreatedDBObject(def,true);
 var line=new Line(Point3d.Origin,new Point3d(10,10,0));def.AppendEntity(line);tr.AddNewlyCreatedDBObject(line,true);
 var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
 var a=new BlockReference(Point3d.Origin,def.ObjectId);first=space.AppendEntity(a);tr.AddNewlyCreatedDBObject(a,true);
 var b=new BlockReference(new Point3d(20,0,0),def.ObjectId);second=space.AppendEntity(b);tr.AddNewlyCreatedDBObject(b,true);b.LayerId=lockedLayer;
 foreach(var br in new[]{a,b})foreach(var tag in new[]{"WRITE","KEEP"}){var attr=new AttributeReference{Tag=tag,TextString="before"};br.AttributeCollection.AppendAttribute(attr);tr.AddNewlyCreatedDBObject(attr,true);}
 layer.IsLocked=true;tr.Commit();}
 var entry=assembly.GetType("TitleAttributeBatch.Core.Entry",true);
 Call(entry,"ApplyTargets",doc,new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"write","after"}},new ObjectId[]{first,second});
 using(var tr=doc.Database.TransactionManager.StartTransaction()){
 foreach(var id in new[]{first,second}){var br=(BlockReference)tr.GetObject(id,OpenMode.ForRead);foreach(ObjectId aid in br.AttributeCollection){var at=(AttributeReference)tr.GetObject(aid,OpenMode.ForRead);Check(at.TextString==(id==first&&at.Tag=="WRITE"?"after":"before"),"Attribute tag matching/unselected or locked value failed");}}
 tr.Commit();}
 log.AppendLine("PASS attribute write: case-insensitive selected tags, unselected values preserved, locked frame skipped");
 }
}
