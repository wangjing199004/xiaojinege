using System; using System.IO; using System.Text; using System.Reflection; using System.Collections; using System.Linq;
using Autodesk.AutoCAD.Runtime; using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry; using Autodesk.AutoCAD.ApplicationServices;
public class DirectoryAlignmentHostRegression
{
 [CommandMethod("DIRECTORY_ALIGNMENT_TEST")] public void Run()
 {
  string root=@"F:\Codedemo\XJG批量打印_fix";var log=new StringBuilder();
  try {
   var a=Assembly.Load(File.ReadAllBytes(Path.Combine(root,"local-artifacts","installer-v30","HP","AutoCAD2015-2024","AcadBatchPlot.Runtime.dll")));
   var jt=a.GetType("ZwcadBatchPlot.PlotJob");var list=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(jt));
   var job=Activator.CreateInstance(jt);jt.GetProperty("Title").SetValue(job,"TITLE_ALIGNMENT_123",null);jt.GetProperty("DrawingNumber").SetValue(job,"N-01",null);list.Add(job);
   var settings=a.GetType("ZwcadBatchPlot.AppSettingsStore").GetMethod("Default").Invoke(null,null);var st=settings.GetType();
   var columns=(IList)st.GetProperty("DirectoryColumns").GetValue(settings,null);int title=-1;for(int i=0;i<columns.Count;i++)if((string)columns[i].GetType().GetProperty("Key").GetValue(columns[i],null)=="Title")title=i;
   if(title<0)throw new System.Exception("Title column missing");double h=(double)st.GetProperty("DirectoryTextHeight").GetValue(settings,null);
   var doc=Application.DocumentManager.MdiActiveDocument;var gen=a.GetType("ZwcadBatchPlot.DirectoryTableGenerator").GetMethod("Generate");
   st.GetProperty("DirectoryUseLinesAndText").SetValue(settings,false,null);gen.Invoke(null,new object[]{doc,list,settings,Point3d.Origin});
   using(var tr=doc.Database.TransactionManager.StartTransaction()) {var model=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForRead);var table=model.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<Table>().Single();
    if(table.Cells[1,title].Alignment!=CellAlignment.MiddleLeft)throw new System.Exception("Native table title not left aligned");
#pragma warning disable 0618
    if(Math.Abs(table.GetMargin(1,title,CellMargins.Left)-h)>0.01)throw new System.Exception("Native table inset differs from one text height");
#pragma warning restore 0618
   }
   log.AppendLine("PASS ordinary native table: title body middle-left, horizontal margin equals text height");
   st.GetProperty("DirectoryUseLinesAndText").SetValue(settings,true,null);gen.Invoke(null,new object[]{doc,list,settings,new Point3d(100000,0,0)});
   using(var tr=doc.Database.TransactionManager.StartTransaction()){var model=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForRead);var entities=model.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead)).ToList();var text=entities.OfType<DBText>().Single(t=>t.TextString=="TITLE_ALIGNMENT_123");
    if(text.HorizontalMode!=TextHorizontalMode.TextLeft)throw new System.Exception("Loose title not left aligned");
    var left=entities.OfType<Line>().Where(l=>Math.Abs(l.StartPoint.X-l.EndPoint.X)<0.01&&l.StartPoint.X<text.AlignmentPoint.X).Max(l=>l.StartPoint.X);
    if(Math.Abs(text.AlignmentPoint.X-left-text.Height)>0.01)throw new System.Exception("Loose title inset differs from one text height");
   }
   log.AppendLine("PASS ordinary line/text table: title left aligned, inset equals one text height");
  }catch(System.Exception ex){log.AppendLine("FAIL "+ex);}
  File.WriteAllText(Path.Combine(root,"local-artifacts","hp-template-inspect","ordinary-alignment.txt"),log.ToString(),Encoding.UTF8);
  if(System.Diagnostics.Process.GetCurrentProcess().ProcessName.Equals("accoreconsole",StringComparison.OrdinalIgnoreCase))Environment.Exit(0);
 }
}
