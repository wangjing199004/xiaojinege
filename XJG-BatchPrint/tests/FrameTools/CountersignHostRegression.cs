using System;using System.IO;using System.Reflection;using System.Text;using System.Collections;using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;using Autodesk.AutoCAD.Geometry;using Autodesk.AutoCAD.Runtime;
public class CounterTest {
static string root=@"F:\Codedemo\XJG批量打印_fix";static BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
static void Check(bool b,string m){if(!b)throw new System.Exception(m);}static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
[CommandMethod("COUNTER_TEST")]public void Run(){var log=new StringBuilder();try{
var asm=Assembly.Load(File.ReadAllBytes(Path.Combine(root,@"local-artifacts\installer-v32\HP\AutoCAD2015-2024\AcadBatchPlot.Runtime.dll")));
var paths=asm.GetType("ZwcadBatchPlot.PluginRuntimePaths");if(paths!=null){foreach(var f in paths.GetFields(F))if(f.Name.IndexOf("base",StringComparison.OrdinalIgnoreCase)>=0&&f.FieldType==typeof(string)&&!f.IsInitOnly)f.SetValue(null,Path.Combine(root,@"local-artifacts\countersign\profile"));}
var c=asm.GetType("CadSignature.Commands");
var profile=Path.Combine(root,@"local-artifacts\countersign\profile-rules");Directory.CreateDirectory(profile);asm.GetType("CadSignature.CoreEntry").GetField("BaseDirectory",F).SetValue(null,profile);var zoneType=c.GetNestedType("Zone",F);var slotType=c.GetNestedType("Slot",F);var personType=asm.GetType("CadSignature.PersonEntry");
foreach(var file in new[]{"A0+图框.dwg","A1+图框.dwg","A2+图框.dwg","封面.dwg","目录签章框.dwg"})using(var db=new Database(true,true)){
var prev=HostApplicationServices.WorkingDatabase;HostApplicationServices.WorkingDatabase=db;try{
ObjectId def;using(var source=new Database(false,true)){source.ReadDwgFile(Path.Combine(root,"目录",file),FileOpenMode.OpenForReadAndAllShare,true,"");source.CloseInput(true);def=db.Insert("fixture_"+Path.GetFileNameWithoutExtension(file),source,false);}
using(var tr=db.TransactionManager.StartTransaction()){
var space=(BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);var br=new BlockReference(Point3d.Origin,def);space.AppendEntity(br);tr.AddNewlyCreatedDBObject(br,true);
var args=new object[]{br,tr,null};Check((bool)c.GetMethod("TryCountersignCenters",F).Invoke(null,args),"labels missing "+file);var expected=(Point3d[])args[2];
Check(expected[0].Y>expected[1].Y&&expected[1].Y>expected[2].Y,"row order");Check(Math.Abs(expected[0].X-expected[2].X)<1e-6,"column alignment");
br.Rotation=.37;br.ScaleFactors=new Scale3d(2);br.Position=new Point3d(300000,400000,0);
args=new object[]{br,tr,null};Check((bool)c.GetMethod("TryCountersignCenters",F).Invoke(null,args),"transformed labels missing");var actual=(Point3d[])args[2];for(int i=0;i<3;i++)Check(actual[i].DistanceTo(expected[i].TransformBy(br.BlockTransform))<1e-5,"transform mismatch");
var z=Activator.CreateInstance(zoneType,true);Set(z,"Id",7);var ext=br.GeometricExtents;Set(z,"Left",ext.MinPoint.X);Set(z,"Right",ext.MaxPoint.X);Set(z,"Bottom",ext.MinPoint.Y);Set(z,"Top",ext.MaxPoint.Y);
var slots=(IList)zoneType.GetField("Slots",F).GetValue(z);var people=Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),personType));var add=people.GetType().GetMethod("Add");
foreach(var role in new[]{"项目负责人","专业负责人","设计"}){var slot=Activator.CreateInstance(slotType,true);Set(slot,"Role",role);Set(slot,"Kind",file=="封面.dwg"?"SIGN":"NAME");Set(slot,"IsCover",file=="封面.dwg");Set(slot,"Height",14.0);Set(slot,"Width",300.0);Set(slot,"Rotation",.37);slots.Add(slot);var person=Activator.CreateInstance(personType,true);Set(person,"Name",role=="设计"?"王五":role=="项目负责人"?"张三":"李四");add.Invoke(people,new object[]{role,person});}
c.GetMethod("EnsureLayers",F).Invoke(null,new object[]{db,tr});
for(int pass=0;pass<(file=="封面.dwg"?3:2);pass++){
if(file=="封面.dwg" && pass==1)foreach(var slot in slots)Set(slot,"Height",1400.0); // Enlarging the signature rows must not enlarge upper real names.
if(file=="封面.dwg" && pass==2){br.ScaleFactors=new Scale3d(1);ext=br.GeometricExtents;Set(z,"Left",ext.MinPoint.X);Set(z,"Right",ext.MaxPoint.X);Set(z,"Bottom",ext.MinPoint.Y);Set(z,"Top",ext.MaxPoint.Y);var coverArgs=new object[]{br,tr,null};Check((bool)c.GetMethod("TryCountersignCenters",F).Invoke(null,coverArgs),"cover scale centers");actual=(Point3d[])coverArgs[2];}
c.GetMethod("AddCountersignNames",F).Invoke(null,new object[]{db,space,tr,z,people});int count=0;foreach(ObjectId id in space){if(id.IsErased)continue;var text=tr.GetObject(id,OpenMode.ForRead) as DBText;if(text==null)continue;count++;Check(text.Justify==AttachmentPoint.MiddleCenter,"alignment");Check(Math.Abs(text.Height-(file=="封面.dwg"?(pass==2?300:600):6))<1e-5,"name size differs");Check(Math.Abs(text.Rotation-.37)<1e-6,"rotation");bool hit=false;foreach(var point in actual)if(text.AlignmentPoint.DistanceTo(point)<1e-5)hit=true;Check(hit,"wrong name location");}Check(count==3,"repeat created duplicates or no names: "+count);}

if(file=="目录签章框.dwg"){
Check((bool)c.GetMethod("IsDirectoryFrame",F).Invoke(null,new object[]{br,tr}),"Directory type missing");
// A complete old lower signature zone and a legacy name must be removed by RunSign.
var midpoint=new Point3d((ext.MinPoint.X+ext.MaxPoint.X)/2,(ext.MinPoint.Y+ext.MaxPoint.Y)/2,0);
c.GetMethod("CreateZoneMarkersOriented",F).Invoke(null,new object[]{db,tr,midpoint,new Vector3d(70,0,0),new Vector3d(0,-49,0),false});
var legacy=new DBText{TextString="OLD LOWER NAME",Position=midpoint+new Vector3d(20,-20,0),Height=3,Layer="CAD_签名"};space.AppendEntity(legacy);tr.AddNewlyCreatedDBObject(legacy,true);
}
tr.Commit();}
if(file=="目录签章框.dwg"){
var result=(string)c.GetMethod("RunSign",F).Invoke(null,null);
using(var tr=db.TransactionManager.StartTransaction()){
var space=(BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForRead);int texts=0,signs=0;foreach(ObjectId id in space){var ent=tr.GetObject(id,OpenMode.ForRead) as Entity;if(ent==null||ent.Layer!="CAD_签名")continue;if(ent is DBText){texts++;Check(((DBText)ent).TextString!="OLD LOWER NAME","Old lower name retained");}else signs++;}Check(texts==3&&signs==0,"Directory must have only three upper names: "+texts+"/"+signs);}
log.AppendLine("PASS directory RunSign without signature files: only three upper names, old lower name removed");
}
{log.AppendLine("PASS "+file+": three text-only names, label centers, nested blocks, rotation/scale, matching height, repeat replaces");}
}finally{HostApplicationServices.WorkingDatabase=prev;}}}
catch(System.Exception ex){log.AppendLine("FAIL "+ex);}File.WriteAllText(Path.Combine(root,@"local-artifacts\countersign\result.txt"),log.ToString());}
}
