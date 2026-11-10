using System;using System.IO;using System.Linq;using System.Text;using System.Reflection;using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;using Autodesk.AutoCAD.DatabaseServices;using Autodesk.AutoCAD.Geometry;
public class StretchTemplateHostRegression {
 static string root=@"F:\Codedemo\XJG批量打印_fix";static BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
 static StringBuilder log=new StringBuilder();
 static void Check(bool ok,string reason){if(!ok)throw new System.Exception(reason);}
 static ObjectId Insert(Assembly a,Database db,string id,Point3d p){return (ObjectId)a.GetType("ZwcadBatchPlot.StandardFrameInserter",true).GetMethod("Insert",Flags).Invoke(null,new object[]{db,db.CurrentSpaceId,id,p});}
 static Dictionary<string,Point3d> AtDistance(Database db,ObjectId id,bool maximum){
 using(var tr=db.TransactionManager.StartTransaction()){
 var layers=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);foreach(ObjectId lid in layers){var layer=(LayerTableRecord)tr.GetObject(lid,OpenMode.ForWrite);layer.IsLocked=false;}
 var br=(BlockReference)tr.GetObject(id,OpenMode.ForWrite);bool changed=false;
 foreach(DynamicBlockReferenceProperty prop in br.DynamicBlockReferencePropertyCollection)if(prop.PropertyName=="距离1"){
 var values=prop.GetAllowedValues().OrderBy(v=>Convert.ToDouble(v)).ToArray();prop.Value=maximum?values.Last():values.First();changed=true;}
 Check(changed,"Distance property missing");var bounds=br.GeometricExtents;var inverse=br.BlockTransform.Inverse();
 var points=new Dictionary<string,Point3d>(StringComparer.OrdinalIgnoreCase);
 foreach(ObjectId aid in br.AttributeCollection){var a=(AttributeReference)tr.GetObject(aid,OpenMode.ForRead);points[a.Tag]=a.Position.TransformBy(inverse);}
 var def=(BlockTableRecord)tr.GetObject(br.BlockTableRecord,OpenMode.ForRead);
 foreach(ObjectId eid in def){var a=tr.GetObject(eid,OpenMode.ForRead)as AttributeDefinition;if(a!=null&&!points.ContainsKey(a.Tag))points[a.Tag]=a.Position;}
 tr.Commit();return points;
 }}
 static Dictionary<string,Vector3d> Deltas(Database db,ObjectId id){var min=AtDistance(db,id,false);var max=AtDistance(db,id,true);return min.ToDictionary(x=>x.Key,x=>max[x.Key]-x.Value,StringComparer.OrdinalIgnoreCase);}
 static void Compare(Dictionary<string,Vector3d> expected,Dictionary<string,Vector3d> actual,string name){
 foreach(var pair in expected){Check(actual.ContainsKey(pair.Key),"Lost attribute "+pair.Key);Check((pair.Value-actual[pair.Key]).Length<0.01,"Stretch displacement differs from original: "+name+"/"+pair.Key+" expected="+pair.Value+" actual="+actual[pair.Key]);}
 Check(actual.ContainsKey("图名"),"Title missing");
 log.AppendLine("PASS "+name+": "+actual.Count+" attributes match original stretch displacements; moved="+string.Join(",",actual.Where(x=>x.Value.Length>0.01).Select(x=>x.Key))+"; title delta="+actual["图名"]);
 }
 [CommandMethod("STRETCH_TEST")]public void Run(){try{
 var current=Assembly.Load(File.ReadAllBytes(Path.Combine(root,"bin-update-v27","AutoCAD2015-2024","AcadBatchPlot.Runtime.dll")));
 var old=Assembly.Load(File.ReadAllBytes(Path.Combine(root,"bin-update-v26","AutoCAD2015-2024","AcadBatchPlot.Runtime.dll")));
 foreach(var entry in new[]{new[]{"a0-plus","A0+图框"},new[]{"a1-plus","A1+图框"},new[]{"a2-plus","A2+图框"}}){var id=entry[0];var name=entry[1];
 Dictionary<string,Vector3d> expected;
 using(var source=new Database(false,true)){
 var path=Path.Combine(root,"目录",name+".dwg");var original=File.ReadAllBytes(path);source.ReadDwgFile(path,FileOpenMode.OpenForReadAndAllShare,true,"");source.CloseInput(true);
 var previous=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=source;
 try{ObjectId frame;using(var tr=source.TransactionManager.StartTransaction()){var bt=(BlockTable)tr.GetObject(source.BlockTableId,OpenMode.ForRead);var model=(BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],OpenMode.ForRead);frame=model.Cast<ObjectId>().Single();}
 expected=Deltas(source,frame);
 }finally{HostApplicationServices.WorkingDatabase=previous;}Check(original.SequenceEqual(File.ReadAllBytes(path)),"Original template changed");}
 using(var db=new Database(true,true)){
 var previous=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=db;
 try{
 var first=Insert(current,db,id,new Point3d(5000,6000,0));Compare(expected,Deltas(db,first),name+" fresh");
 var again=Insert(current,db,id,new Point3d(500000,0,0));using(var tr=db.TransactionManager.StartTransaction()){var a=(BlockReference)tr.GetObject(first,OpenMode.ForRead);var b=(BlockReference)tr.GetObject(again,OpenMode.ForRead);Check(a.DynamicBlockTableRecord==b.DynamicBlockTableRecord,"Repeat insert did not reuse current version");}
 }finally{HostApplicationServices.WorkingDatabase=previous;}}
 using(var db=new Database(true,true)){
 var previous=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=db;
 try{
 var existing=Insert(old,db,id,Point3d.Origin);var oldBefore=AtDistance(db,existing,true);ObjectId oldDef;int beforeCount;
 using(var tr=db.TransactionManager.StartTransaction()){var b=(BlockReference)tr.GetObject(existing,OpenMode.ForWrite);oldDef=b.DynamicBlockTableRecord;foreach(ObjectId aid in b.AttributeCollection){var a=(AttributeReference)tr.GetObject(aid,OpenMode.ForWrite);if(a.Tag=="项目名称")a.TextString="既有内容";}beforeCount=((BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead)).Cast<ObjectId>().Count();tr.Commit();}
 oldBefore=AtDistance(db,existing,true); // Capture after text alignment has settled.
 var cancelled=(ObjectId)current.GetType("ZwcadBatchPlot.StandardFrameInserter",true).GetMethod("InsertWithPlacement",Flags).Invoke(null,new object[]{db,db.CurrentSpaceId,id,(Func<BlockReference,Transaction,bool>)((frame,tr)=>false)});
 Check(cancelled.IsNull,"Cancel placed a frame");using(var tr=db.TransactionManager.StartTransaction()){Check(((BlockTableRecord)tr.GetObject(oldDef,OpenMode.ForRead)).Name==name,"Cancel renamed old definition");Check(((BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead)).Cast<ObjectId>().Count()==beforeCount,"Cancel left version definitions");}
 var inserted=Insert(current,db,id,new Point3d(1000000,0,0));Compare(expected,Deltas(db,inserted),name+" in old drawing");
 using(var tr=db.TransactionManager.StartTransaction()){
 var oldFrame=(BlockReference)tr.GetObject(existing,OpenMode.ForRead);var newFrame=(BlockReference)tr.GetObject(inserted,OpenMode.ForRead);
 Check(oldFrame.DynamicBlockTableRecord!=newFrame.DynamicBlockTableRecord,"New stretch graph reused old definition");
 Check(((BlockTableRecord)tr.GetObject(newFrame.DynamicBlockTableRecord,OpenMode.ForRead)).Name==name,"New name not canonical");
 var getName=current.GetType("ZwcadBatchPlot.CadTextExtractor",true).GetMethod("GetBlockName",Flags);
 Check((string)getName.Invoke(null,new object[]{oldFrame,tr})==name && (string)getName.Invoke(null,new object[]{newFrame,tr})==name,"Library names differ by template version");
 foreach(ObjectId aid in oldFrame.AttributeCollection){var a=(AttributeReference)tr.GetObject(aid,OpenMode.ForRead);Check(a.Position.TransformBy(oldFrame.BlockTransform.Inverse()).DistanceTo(oldBefore[a.Tag])<0.01,"Existing attribute position changed: "+a.Tag+" before="+oldBefore[a.Tag]+" after="+a.Position.TransformBy(oldFrame.BlockTransform.Inverse()));if(a.Tag=="项目名称")Check(a.TextString=="既有内容","Existing content changed");}
 }
 var repeated=Insert(current,db,id,new Point3d(2000000,0,0));using(var tr=db.TransactionManager.StartTransaction()){var a=(BlockReference)tr.GetObject(inserted,OpenMode.ForRead);var b=(BlockReference)tr.GetObject(repeated,OpenMode.ForRead);Check(a.DynamicBlockTableRecord==b.DynamicBlockTableRecord,"Repeat version update created another definition");}
 log.AppendLine("PASS "+name+": latest graph in old drawing; cancel cleanup; old content/positions preserved; common library identity; repeated version reuse");
 }finally{HostApplicationServices.WorkingDatabase=previous;}}
 }
 }catch(System.Exception ex){log.AppendLine("FAIL "+ex);}
 File.WriteAllText(Path.Combine(root,"local-artifacts","integration-host","stretch-regression.txt"),log.ToString(),Encoding.UTF8);
 }
}
