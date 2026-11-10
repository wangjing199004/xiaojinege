using System;using System.IO;using System.Linq;using System.Text;using System.Reflection;using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;using Autodesk.AutoCAD.DatabaseServices;using Autodesk.AutoCAD.Geometry;
public class OneClickTitleHostRegression {
 static BindingFlags Flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 static string root=@"F:\Codedemo\XJG批量打印_fix";static StringBuilder log=new StringBuilder();
 static void Check(bool ok,string reason){if(!ok)throw new System.Exception(reason);}
 static string Value(Database db,ObjectId frame,string tag){using(var tr=db.TransactionManager.StartTransaction()){var br=(BlockReference)tr.GetObject(frame,OpenMode.ForRead);foreach(ObjectId id in br.AttributeCollection){var a=(AttributeReference)tr.GetObject(id,OpenMode.ForRead);if(a.Tag==tag)return a.TextString;}return null;}}
 static ObjectId Make(Database db, bool withTitle, bool lockedAttribute){
 using(var tr=db.TransactionManager.StartTransaction()){
 var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);var def=new BlockTableRecord{Name="TITLE_TEST_"+Guid.NewGuid().ToString("N")};bt.Add(def);tr.AddNewlyCreatedDBObject(def,true);
 var line=new Line(Point3d.Origin,new Point3d(10,10,0));def.AppendEntity(line);tr.AddNewlyCreatedDBObject(line,true);
 var space=(BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);var br=new BlockReference(Point3d.Origin,def.ObjectId);space.AppendEntity(br);tr.AddNewlyCreatedDBObject(br,true);
 foreach(string tag in withTitle?new[]{"图名","图号"}:new[]{"图号"}){var a=new AttributeReference{Tag=tag,TextString="original"};br.AttributeCollection.AppendAttribute(a);tr.AddNewlyCreatedDBObject(a,true);}
 if(lockedAttribute){var layers=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForWrite);var layer=new LayerTableRecord{Name="LOCK_"+Guid.NewGuid().ToString("N")};layers.Add(layer);tr.AddNewlyCreatedDBObject(layer,true);var a=new AttributeReference{Tag="图名",TextString="locked"};br.AttributeCollection.AppendAttribute(a);tr.AddNewlyCreatedDBObject(a,true);a.LayerId=layer.ObjectId;layer.IsLocked=true;}
 tr.Commit();return br.ObjectId;
 }}
 [CommandMethod("TITLE_TEST")]public void Run(){try{
 var asm=Assembly.Load(File.ReadAllBytes(Path.Combine(root,"bin-update-v27","AutoCAD2015-2024","AcadBatchPlot.Runtime.dll")));
 var service=asm.GetType("ZwcadBatchPlot.OneClickDrawingTitle",true);var read=service.GetMethod("ReadText",Flags);var assign=service.GetMethod("Assign",Flags);
 using(var db=new Database(true,true)){
 var previous=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=db;
 try{
 using(var text=new DBText{TextString="平面图{甲}\\A"})Check((string)read.Invoke(null,new object[]{text})=="平面图{甲}\\A","DBText literal read failed");
 using(var text=new MText{Contents=@"{\C1;楼层图}\P详图"}){var plain=(string)read.Invoke(null,new object[]{text});Check(plain.Contains("楼层图")&&plain.Contains("详图")&&!plain.Contains("\\C1"),"MText formatting leaked");}
 using(var text=new AttributeReference{Tag="任意",TextString="属性内容"})Check((string)read.Invoke(null,new object[]{text})=="属性内容","Attribute text read failed");
 var frame=Make(db,true,false);Check((int)assign.Invoke(null,new object[]{db,frame,"  新图名{甲}\\A\n详图  "})==1,"Assign count failed");
 Check(Value(db,frame,"图名")=="新图名{甲}\\A 详图"&&Value(db,frame,"图号")=="original","Wrong attribute updated or literal/newline lost");
 var multiline=Make(db,false,false);
 using(var tr=db.TransactionManager.StartTransaction()){var br=(BlockReference)tr.GetObject(multiline,OpenMode.ForWrite);var a=new AttributeReference{Tag="图名",IsMTextAttribute=true};br.AttributeCollection.AppendAttribute(a);tr.AddNewlyCreatedDBObject(a,true);tr.Commit();}
 assign.Invoke(null,new object[]{db,multiline,"首行{甲}\\A\n第二行"});
 using(var tr=db.TransactionManager.StartTransaction()){var br=(BlockReference)tr.GetObject(multiline,OpenMode.ForRead);var a=br.AttributeCollection.Cast<ObjectId>().Select(x=>(AttributeReference)tr.GetObject(x,OpenMode.ForRead)).Single(x=>x.Tag=="图名");using(var text=a.MTextAttribute){var plain=text.Text.Replace("\r\n","\n").Replace("\r","\n");Check(plain=="首行{甲}\\A\n第二行","Multiline title escaped literals or lost paragraph");}}
 var absent=Make(db,false,false);bool rejected=false;try{assign.Invoke(null,new object[]{db,absent,"no"});}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidOperationException;}Check(rejected&&Value(db,absent,"图号")=="original","Missing title was not rejected");
 var locked=Make(db,true,true);rejected=false;try{assign.Invoke(null,new object[]{db,locked,"no"});}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidOperationException;}Check(rejected&&Value(db,locked,"图名")=="original","Locked duplicate title allowed partial changes");
 var insert=asm.GetType("ZwcadBatchPlot.StandardFrameInserter",true).GetMethod("Insert",Flags);
 foreach(var id in new[]{"a0-plus","a1-plus","a2-plus"}){
 var added=(ObjectId)insert.Invoke(null,new object[]{db,db.CurrentSpaceId,id,new Point3d(1000000,1000000,0)});
 Check(Value(db,added,"图名")=="","Updated template does not carry blank title: "+id);
 assign.Invoke(null,new object[]{db,added,"模板图名"});Check(Value(db,added,"图名")=="模板图名","Updated template title write failed");
 log.AppendLine("PASS updated encrypted template "+id+": blank title, title assignment, same tag only");
 }
 log.AppendLine("PASS: DBText/MText/attribute read; literals and line breaks; only title changed; missing tag rejected; locked duplicate prevents partial changes");
 }finally{HostApplicationServices.WorkingDatabase=previous;}}
 var old=Assembly.Load(File.ReadAllBytes(Path.Combine(root,"bin-update-v25","AutoCAD2015-2024","AcadBatchPlot.Runtime.dll")));
 using(var db=new Database(true,true)){
 var previous=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=db;
 try{foreach(var id in new[]{"a0-plus","a1-plus","a2-plus"}){
 var oldInsert=old.GetType("ZwcadBatchPlot.StandardFrameInserter",true).GetMethod("Insert",Flags);var oldId=(ObjectId)oldInsert.Invoke(null,new object[]{db,db.CurrentSpaceId,id,Point3d.Origin});
 string expectedDistance="";
 using(var tr=db.TransactionManager.StartTransaction()){var br=(BlockReference)tr.GetObject(oldId,OpenMode.ForWrite);foreach(DynamicBlockReferenceProperty prop in br.DynamicBlockReferencePropertyCollection)if(prop.PropertyName=="距离1"){var values=prop.GetAllowedValues();prop.Value=values.OrderBy(v=>Convert.ToDouble(v)).Last();expectedDistance=prop.Value.ToString();}foreach(ObjectId aid in br.AttributeCollection){var at=(AttributeReference)tr.GetObject(aid,OpenMode.ForWrite);if(at.Tag=="项目名称")at.TextString="原有项目内容";}tr.Commit();}
 var cancelled=(ObjectId)asm.GetType("ZwcadBatchPlot.StandardFrameInserter",true).GetMethod("InsertWithPlacement",Flags).Invoke(null,new object[]{db,db.CurrentSpaceId,id,(Func<BlockReference,Transaction,bool>)((preview,tr)=>false)});
 Check(cancelled.IsNull,"Cancelled frame unexpectedly placed");
 using(var tr=db.TransactionManager.StartTransaction()){var br=(BlockReference)tr.GetObject(oldId,OpenMode.ForRead);var def=(BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord,OpenMode.ForRead);Check(!def.Cast<ObjectId>().Select(x=>tr.GetObject(x,OpenMode.ForRead)).OfType<AttributeDefinition>().Any(a=>a.Tag=="图名"),"Cancelled insert changed old definition");}
 var added=(ObjectId)asm.GetType("ZwcadBatchPlot.StandardFrameInserter",true).GetMethod("Insert",Flags).Invoke(null,new object[]{db,db.CurrentSpaceId,id,new Point3d(1000000,0,0)});
 Check(Value(db,added,"图名")=="","New instance in old drawing lost title");
 Check(Value(db,oldId,"项目名称")=="原有项目内容","Definition synchronization changed existing instance content");
 using(var tr=db.TransactionManager.StartTransaction()){var br=(BlockReference)tr.GetObject(oldId,OpenMode.ForRead);foreach(DynamicBlockReferenceProperty prop in br.DynamicBlockReferencePropertyCollection)if(prop.PropertyName=="距离1")Check(prop.Value.ToString()==expectedDistance,"Synchronization reset existing dynamic size");}

 using(var tr=db.TransactionManager.StartTransaction()){var br=(BlockReference)tr.GetObject(added,OpenMode.ForRead);var def=(BlockTableRecord)tr.GetObject(br.IsDynamicBlock?br.DynamicBlockTableRecord:br.BlockTableRecord,OpenMode.ForRead);bool has=def.Cast<ObjectId>().Select(x=>tr.GetObject(x,OpenMode.ForRead)).OfType<AttributeDefinition>().Any(a=>a.Tag=="图名");Check(has,"Existing shared definition lost title on attribute synchronization");log.AppendLine("PASS existing definition "+id+": title-definition synchronized, stable name");}
 }}finally{HostApplicationServices.WorkingDatabase=previous;}}
 var aliases=(System.Collections.IEnumerable)asm.GetType("ZwcadBatchPlot.CommandAliasManager").GetField("AliasableCommands",Flags).GetValue(null);bool found=false;foreach(var a in aliases){if((string)a.GetType().GetProperty("CommandName",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(a,null)=="TKATITLE")found=true;}Check(found,"Shortcut row missing");
 log.AppendLine("PASS shortcut command TKATITLE present");
 }catch(System.Exception ex){log.AppendLine("FAIL "+ex);}
 File.WriteAllText(Path.Combine(root,"local-artifacts","integration-host","title-regression.txt"),log.ToString(),Encoding.UTF8);
 }
}
