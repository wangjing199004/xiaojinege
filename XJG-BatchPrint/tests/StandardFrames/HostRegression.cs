using System;using System.IO;using System.Text;using System.Linq;using System.Reflection;using System.Collections;using System.Collections.Generic;using System.Security.Cryptography;
using Autodesk.AutoCAD.Runtime;using Autodesk.AutoCAD.DatabaseServices;using Autodesk.AutoCAD.Geometry;
public class StandardFrameHostRegression {
 [CommandMethod("STD_TEST")] public void Run(){
 var root=Environment.GetEnvironmentVariable("XJG_HP_TEST_ROOT");if(string.IsNullOrWhiteSpace(root))root=@"F:\Codedemo\XJG批量打印_fix";
 var log=new StringBuilder();try{
 var assembly=Assembly.LoadFrom(Path.Combine(root,"bin-update-v23","AutoCAD2015-2024","AcadBatchPlot.dll"));
 var insert=assembly.GetType("ZwcadBatchPlot.StandardFrameInserter",true).GetMethod("Insert",BindingFlags.Public|BindingFlags.Static);
 var catalog=(IEnumerable)assembly.GetType("ZwcadBatchPlot.StandardFrameCatalog",true).GetProperty("Items",BindingFlags.Public|BindingFlags.Static).GetValue(null,null);
 foreach(var item in catalog){var id=(string)item.GetType().GetProperty("Id").GetValue(item,null);var name=(string)item.GetType().GetProperty("Name").GetValue(item,null);var sourcePath=Path.Combine(root,"目录",name+".dwg");var hash=File.ReadAllBytes(sourcePath);
 using(var original=new Database(false,true))using(var db=new Database(true,true)){
 original.ReadDwgFile(sourcePath,FileOpenMode.OpenForReadAndAllShare,true,"");original.CloseInput(true);
 bool dynamic=false;int attributes=0;Point3d originalOrigin=Point3d.Origin;var properties=new Dictionary<string,string>();
 using(var tr=original.TransactionManager.StartTransaction()){var bt=(BlockTable)tr.GetObject(original.BlockTableId,OpenMode.ForRead);var model=(BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],OpenMode.ForRead);var br=model.Cast<ObjectId>().Select(o=>tr.GetObject(o,OpenMode.ForRead)).OfType<BlockReference>().Single();dynamic=br.IsDynamicBlock;attributes=br.AttributeCollection.Count;originalOrigin=((BlockTableRecord)tr.GetObject(br.BlockTableRecord,OpenMode.ForRead)).Origin;if(dynamic)foreach(DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)properties[p.PropertyName]=p.PropertyName=="距离1"?p.GetAllowedValues().OrderBy(v=>Convert.ToDouble(v)).First().ToString():p.Value.ToString();tr.Commit();}
 var previous=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=db;
 try{ObjectId space,layerId;using(var tr=db.TransactionManager.StartTransaction()){var layers=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForWrite);var layer=new LayerTableRecord{Name="FRAME_CURRENT_LAYER"};layerId=layers.Add(layer);tr.AddNewlyCreatedDBObject(layer,true);db.Clayer=layerId;layer.IsLocked=true;var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);space=bt[BlockTableRecord.ModelSpace];tr.Commit();}
 var point=new Point3d(12345.5,67890.25,0);var insertedId=(ObjectId)insert.Invoke(null,new object[]{db,space,id,point});
 ObjectId blockId;bool changedDynamic=false;
 using(var tr=db.TransactionManager.StartTransaction()){var inserted=(BlockReference)tr.GetObject(insertedId,OpenMode.ForWrite,false,true);blockId=inserted.BlockTableRecord;
 var bounds=inserted.GeometricExtents;var center=bounds.MinPoint+(bounds.MaxPoint-bounds.MinPoint)*0.5;
 if(((BlockTableRecord)tr.GetObject(inserted.BlockTableRecord,OpenMode.ForRead)).Origin.DistanceTo(originalOrigin)>0.001)throw new System.Exception("Original block origin changed");
 if(inserted.LayerId!=layerId||center.DistanceTo(point)>0.01)throw new System.Exception("Current layer/insertion point failed "+name);
 if(inserted.AttributeCollection.Count!=attributes)throw new System.Exception("Attributes lost "+name);
 if(inserted.IsDynamicBlock!=dynamic)throw new System.Exception("Dynamic block lost "+name);
 if(dynamic){foreach(DynamicBlockReferenceProperty p in inserted.DynamicBlockReferencePropertyCollection){if(!properties.ContainsKey(p.PropertyName)||properties[p.PropertyName]!=p.Value.ToString())throw new System.Exception("Dynamic minimum/other value mismatch");}
 foreach(DynamicBlockReferenceProperty p in inserted.DynamicBlockReferencePropertyCollection){var allowed=p.GetAllowedValues();if(p.ReadOnly||allowed.Length<2)continue;var replacement=allowed.FirstOrDefault(v=>v.ToString()!=p.Value.ToString());if(replacement!=null){p.Value=replacement;changedDynamic=true;break;}}}
 CheckBlock(tr,inserted);var layer=(LayerTableRecord)tr.GetObject(layerId,OpenMode.ForRead);if(!layer.IsLocked||db.Clayer!=layerId)throw new System.Exception("Current layer/lock changed");tr.Commit();}
 var again=(ObjectId)insert.Invoke(null,new object[]{db,space,id,new Point3d(500000,0,0)});
 using(var tr=db.TransactionManager.StartTransaction()){var first=(BlockReference)tr.GetObject(insertedId,OpenMode.ForRead);var second=(BlockReference)tr.GetObject(again,OpenMode.ForRead);var firstDefinition=first.IsDynamicBlock?first.DynamicBlockTableRecord:first.BlockTableRecord;var secondDefinition=second.IsDynamicBlock?second.DynamicBlockTableRecord:second.BlockTableRecord;if(firstDefinition!=secondDefinition)throw new System.Exception("Repeated inserts do not reuse effective definition");if(((BlockTableRecord)tr.GetObject(secondDefinition,OpenMode.ForRead)).Name!=name)throw new System.Exception("Unstable effective block name");if(second.IsDynamicBlock)foreach(DynamicBlockReferenceProperty p in second.DynamicBlockReferencePropertyCollection)if(properties[p.PropertyName]!=p.Value.ToString())throw new System.Exception("Second instance inherited modified size");CheckBlock(tr,second);tr.Commit();}
 // 模拟原生jig的连续更新，然后取消；不得残留图块、定义或改变图层锁。
 int beforeEntities,beforeBlocks;
 using(var tr=db.TransactionManager.StartTransaction()){beforeEntities=((BlockTableRecord)tr.GetObject(space,OpenMode.ForRead)).Cast<ObjectId>().Count();beforeBlocks=((BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead)).Cast<ObjectId>().Count();tr.Commit();}
 var jigType=assembly.GetType("ZwcadBatchPlot.StandardFramePlacementJig",true);
 var cancel=(Func<BlockReference,Transaction,bool>)((preview,tr)=>{
 var owner=(BlockTableRecord)tr.GetObject(preview.OwnerId,OpenMode.ForRead);
 if(owner.IsLayout||preview.OwnerId==space)throw new System.Exception("Preview is in visible drawing space");
 if(((BlockTableRecord)tr.GetObject(space,OpenMode.ForRead)).Cast<ObjectId>().Count()!=beforeEntities)throw new System.Exception("Static frame added before position confirmation");
 var jig=Activator.CreateInstance(jigType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{preview,tr,null},null);
 foreach(var cursor in new[]{new Point3d(-2000,3000,0),new Point3d(1e9,-1e9,0),point}){
 jigType.GetField("_point",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(jig,cursor);
 if(!(bool)jigType.GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(jig,null))throw new System.Exception("Preview update failed");
 var b=preview.GeometricExtents;var c=b.MinPoint+(b.MaxPoint-b.MinPoint)*0.5;if(c.DistanceTo(cursor)>0.01)throw new System.Exception("Preview center does not follow cursor");
 CheckBlock(tr,preview);
 }return false;});
 var cancelled=(ObjectId)assembly.GetType("ZwcadBatchPlot.StandardFrameInserter",true).GetMethod("InsertWithPlacement",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{db,space,id,cancel});
 if(!cancelled.IsNull)throw new System.Exception("Cancelled preview unexpectedly committed");
 using(var tr=db.TransactionManager.StartTransaction()){if(((BlockTableRecord)tr.GetObject(space,OpenMode.ForRead)).Cast<ObjectId>().Count()!=beforeEntities||((BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead)).Cast<ObjectId>().Count()!=beforeBlocks)throw new System.Exception("Cancelled preview left objects or definitions");if(!((LayerTableRecord)tr.GetObject(layerId,OpenMode.ForRead)).IsLocked)throw new System.Exception("Cancel changed layer lock");tr.Commit();}
 db.SaveAs(Path.Combine(root,"local-artifacts","hp-template-inspect","standard-"+id+".dwg"),DwgVersion.AC1024);
 log.AppendLine("PASS "+name+": empty attrs/defaults="+attributes+", all nested entities layer0, insert on locked current layer, center alignment, preserved original origin, preview hidden staging/no static frame/cancel rollback, repeated insert, dynamic="+dynamic+", min-distance="+(properties.ContainsKey("距离1")?properties["距离1"]:"n/a")+", switch="+changedDynamic);
 }finally{HostApplicationServices.WorkingDatabase=previous;}}
 if(!hash.SequenceEqual(File.ReadAllBytes(sourcePath)))throw new System.Exception("Original template changed");
 }
 }catch(System.Exception ex){log.AppendLine("FAIL "+ex);}
 File.WriteAllText(Path.Combine(root,"local-artifacts","hp-template-inspect","standard-regression.txt"),log.ToString(),Encoding.UTF8);}
 static void CheckBlock(Transaction tr,BlockReference root){
 foreach(ObjectId aid in root.AttributeCollection){var a=(AttributeReference)tr.GetObject(aid,OpenMode.ForRead);if(a.Layer!="0"||a.TextString!="")throw new System.Exception("Root attr not empty/layer0");}
 var pending=new Queue<ObjectId>();var visited=new HashSet<ObjectId>();pending.Enqueue(root.BlockTableRecord);if(root.IsDynamicBlock)pending.Enqueue(root.DynamicBlockTableRecord);
 while(pending.Count>0){var bid=pending.Dequeue();if(!visited.Add(bid))continue;var block=(BlockTableRecord)tr.GetObject(bid,OpenMode.ForRead);foreach(ObjectId id in block){var entity=tr.GetObject(id,OpenMode.ForRead)as Entity;if(entity==null)continue;if(entity.Layer!="0")throw new System.Exception("Nested layer not zero: "+entity.Layer);var def=entity as AttributeDefinition;if(def!=null&&def.TextString!="")throw new System.Exception("Default value not empty");var br=entity as BlockReference;if(br!=null){pending.Enqueue(br.BlockTableRecord);if(br.IsDynamicBlock)pending.Enqueue(br.DynamicBlockTableRecord);foreach(ObjectId aid in br.AttributeCollection){var a=(AttributeReference)tr.GetObject(aid,OpenMode.ForRead);if(a.Layer!="0"||a.TextString!="")throw new System.Exception("Nested attr not empty/layer0");}}}}
 }
}
