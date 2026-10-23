using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
public class HpDirectoryHostRegression {
 [CommandMethod("HP_TEST")]
 public void Run() {
 var root=Environment.GetEnvironmentVariable("XJG_HP_TEST_ROOT");
 if(string.IsNullOrWhiteSpace(root))root=@"F:\Codedemo\XJG批量打印_fix";
 var report=new StringBuilder();
 try {
 var assembly=Assembly.LoadFrom(Path.Combine(root,"bin-update-v18","AutoCAD2015-2024","AcadBatchPlot.dll"));
 var type=assembly.GetType("ZwcadBatchPlot.PlotJob",true);
 var insert=assembly.GetType("ZwcadBatchPlot.HpDirectoryExporter",true).GetMethod("Insert",BindingFlags.Public|BindingFlags.Static);
 foreach(var count in new[]{1,27,28,59,60,86,118,119}) {
 var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
 for(int n=1;n<=count;n++){var j=Activator.CreateInstance(type);type.GetProperty("DrawingNumber").SetValue(j,"HP-"+n.ToString("000"),null);type.GetProperty("Title").SetValue(j,n==2?"图名{特殊}\\路径":n==3?new string('长',120):"图名"+n,null);type.GetProperty("PaperName").SetValue(j,"A3",null);type.GetProperty("Revision").SetValue(j,"R1",null);list.Add(j);}
 var skip=Activator.CreateInstance(type);type.GetProperty("Selected").SetValue(skip,false,null);list.Insert(0,skip);
 using(var db=new Database(true,true)) {
 var old=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=db;
 try {
 ObjectId space;
 using(var tr=db.TransactionManager.StartTransaction()) {
 var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);space=bt[BlockTableRecord.ModelSpace];
 var lt=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);((LayerTableRecord)tr.GetObject(lt["0"],OpenMode.ForWrite)).IsLocked=true;tr.Commit();}
 var origin=new Point3d(1234.5,6789.25,0);
 var pageCount=(int)insert.Invoke(null,new object[]{db,space,list,origin});
 var capacity=count<=27?27:59;
 if(pageCount!=(count+capacity-1)/capacity)throw new System.Exception("Page count failed "+count);
 ObjectId firstBlock;string firstNumber;
 using(var tr=db.TransactionManager.StartTransaction()) {
 var model=(BlockTableRecord)tr.GetObject(space,OpenMode.ForRead);
 var entities=model.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead)).ToList();
 var refs=entities.OfType<BlockReference>().OrderBy(r=>r.Position.X).ToList();
 var texts=entities.OfType<DBText>().ToList();
 if(refs.Count!=pageCount||texts.Count!=count*4||entities.OfType<MText>().Any())throw new System.Exception("Must be block plus loose single-line texts");
 if(Math.Abs(refs[0].GeometricExtents.MinPoint.X-origin.X)>0.01||Math.Abs(refs[0].GeometricExtents.MaxPoint.Y-origin.Y)>0.01)throw new System.Exception("Insertion anchor incorrect");
 for(int pi=0;pi<refs.Count;pi++) {
 var br=refs[pi];if(br.AttributeCollection.Count!=7)throw new System.Exception("Header attributes missing");
 var block=(BlockTableRecord)tr.GetObject(br.BlockTableRecord,OpenMode.ForRead);
 var nested=block.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead)).ToList();
 if(nested.OfType<MText>().Any()||nested.OfType<DBText>().Any(t=>t.TextString.StartsWith("HP-")))throw new System.Exception("Body text inside frame block");
 var nums=nested.OfType<DBText>().Where(t=>{int n;return int.TryParse(t.TextString,out n)&&t.TextString==n.ToString();}).Select(t=>int.Parse(t.TextString)).OrderBy(n=>n).ToList();
 if(!nums.SequenceEqual(Enumerable.Range(pi*capacity+1,capacity)))throw new System.Exception("Full serial column failed");
 using(var source=new Database(false,true)) {
 source.ReadDwgFile(Path.Combine(root,"目录",capacity==27?"A4目录.dwg":"A3目录.dwg"),FileOpenMode.OpenForReadAndAllShare,true,"");source.CloseInput(true);
 using(var str=source.TransactionManager.StartTransaction()) {
 var sbt=(BlockTable)str.GetObject(source.BlockTableId,OpenMode.ForRead);
 var sm=(BlockTableRecord)str.GetObject(sbt[BlockTableRecord.ModelSpace],OpenMode.ForRead);
 var sr=sm.Cast<ObjectId>().Select(id=>str.GetObject(id,OpenMode.ForRead)).OfType<BlockReference>().Single();
 var originalAttributes=sr.AttributeCollection.Cast<ObjectId>().Select(id=>(AttributeReference)str.GetObject(id,OpenMode.ForRead)).ToDictionary(ar=>ar.Tag,ar=>(ar.Justify==AttachmentPoint.BaseLeft?ar.Position:ar.AlignmentPoint).TransformBy(sr.BlockTransform.Inverse()));
 if(nested.OfType<AttributeDefinition>().Any(a=>a.TextString!=""))throw new System.Exception("Attribute default value not empty");
 foreach(ObjectId aid in br.AttributeCollection){var ar=(AttributeReference)tr.GetObject(aid,OpenMode.ForRead);if(ar.TextString!="")throw new System.Exception("Generated attribute not empty: "+ar.Tag);var expected=originalAttributes[ar.Tag].TransformBy(br.BlockTransform);var actual=ar.Justify==AttachmentPoint.BaseLeft?ar.Position:ar.AlignmentPoint;if(actual.DistanceTo(expected)>0.1)throw new System.Exception("Empty attribute anchor moved: "+ar.Tag);}

 str.Commit();}}

 foreach(var text in texts.Where(t=>t.TextString.StartsWith("HP-"))) {
 var n=int.Parse(text.TextString.Substring(3));if((n-1)/capacity!=pi)continue;var slot=(n-1)%capacity+1;
 var local=text.AlignmentPoint.TransformBy(br.BlockTransform.Inverse());var right=capacity==59&&slot>32;
 if(right!=(local.X>21000))throw new System.Exception("Column placement failed");
 if(Math.Abs(local.Y-(25900-((slot-1)%(capacity==59?32:27))*800))>2)throw new System.Exception("Row placement failed");
 var title=texts.Single(t=>Math.Abs(t.AlignmentPoint.Y-text.AlignmentPoint.Y)<1&&Math.Abs(t.AlignmentPoint.X-(text.AlignmentPoint.X+6550))<1);
 if(title.GeometricExtents.MaxPoint.X-title.GeometricExtents.MinPoint.X>10902)throw new System.Exception("Single-line title overflow");
 }
 }
 if(count>=2&&!texts.Any(t=>t.TextString=="图名{特殊}\\路径"))throw new System.Exception("Literal text changed");
 var layer=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);if(!((LayerTableRecord)tr.GetObject(layer["0"],OpenMode.ForRead)).IsLocked)throw new System.Exception("Layer lock changed");
 firstBlock=refs[0].BlockTableRecord;firstNumber=texts.First(t=>t.TextString.StartsWith("HP-")).TextString;tr.Commit();}
 // 同一张图重复生成，不能改动旧图块或共用会被重写的图块定义。
 insert.Invoke(null,new object[]{db,space,list,new Point3d(1000000,0,0)});
 using(var tr=db.TransactionManager.StartTransaction()) {
 var model=(BlockTableRecord)tr.GetObject(space,OpenMode.ForRead);var refs=model.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<BlockReference>().ToList();
 if(refs.Count!=pageCount*2||refs.Count(r=>r.BlockTableRecord==firstBlock)!=1)throw new System.Exception("Repeated insert reused frame definition");tr.Commit();}
 var outdir=Path.Combine(root,"local-artifacts","hp-template-inspect");Directory.CreateDirectory(outdir);
 db.SaveAs(Path.Combine(outdir,"insert-"+count+".dwg"),DwgVersion.AC1024);
 report.AppendLine("PASS insert rows="+count+", pages="+pageCount+", template="+(capacity==27?"A4":"A3")+", full serials, loose DBText, literal/long text, selected order, placement, empty attribute defaults/values and preserved anchors, layer locks, repeated insertion");
 } finally {HostApplicationServices.WorkingDatabase=old;}
 }
 }
 } catch(System.Exception ex){report.AppendLine("FAIL "+ex);}
 File.WriteAllText(Path.Combine(root,"local-artifacts","hp-template-inspect","insert-regression.txt"),report.ToString(),Encoding.UTF8);
 }
}
