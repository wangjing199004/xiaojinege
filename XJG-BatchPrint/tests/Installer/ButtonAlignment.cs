using System; using System.IO; using System.Linq; using System.Reflection; using System.Drawing; using System.Windows.Forms; using System.Collections.Generic;
internal static class ButtonAlignment
{
 static Assembly assembly; static int mismatches;
 static IEnumerable<Control> Walk(Control root) { yield return root; foreach(Control child in root.Controls) foreach(var nested in Walk(child)) yield return nested; }
 static void CheckRow(IEnumerable<Button> source,string name) { var buttons=source.Where(b=>b.Visible).ToArray(); if(buttons.Length<2)return; if(buttons.Any(b=>b.Top!=buttons[0].Top || b.Height!=buttons[0].Height)){mismatches++;Console.WriteLine("MISALIGNED "+name+": "+string.Join("; ",buttons.Select(b=>b.Text+" top="+b.Top+" height="+b.Height)));} }
 static void Audit(Form form,string name,string folder,float scale)
 {
  using(form){form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.ShowInTaskbar=false;form.Show();if(scale!=1)form.Scale(new SizeF(scale,scale));Application.DoEvents();
   if(name=="SignaturePicker"){
    if(form.Text!="选择签名笔迹")throw new Exception("Signature picker title has extra suffix");
    var delete=Walk(form).OfType<Button>().Single(b=>b.Text=="删除笔迹");
    assembly.GetType("CadSignature.SignatureVault",true).GetMethod("Save",BindingFlags.Static|BindingFlags.Public).Invoke(null,new object[]{Path.Combine(folder,"isolated-profile"),"测试笔迹.dwg",new byte[]{1,2,3}});
    form.GetType().GetMethod("Reload",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
    var list=Walk(form).OfType<ListView>().Single();list.Items[0].Selected=true;Application.DoEvents();
    if(!delete.Enabled)throw new Exception("Delete action not enabled for selected signature");
   }
   foreach(var container in Walk(form)){
    var flow=container as FlowLayoutPanel;if(flow!=null&&flow.FlowDirection==FlowDirection.LeftToRight&&!flow.WrapContents)CheckRow(flow.Controls.OfType<Button>(),name+" flow");
    var table=container as TableLayoutPanel;if(table!=null)foreach(var group in table.Controls.OfType<Button>().GroupBy(b=>table.GetRow(b)))CheckRow(group,name+" table row"+group.Key);
   }
   if(name=="FrameCalibEditor"){var confirm=Walk(form).OfType<Button>().Single(b=>b.Text=="确认");var cancel=Walk(form).OfType<Button>().Single(b=>b.Text=="取消");CheckRow(new[]{confirm,cancel},name+" footer");}
   if(scale==1)using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(folder,name+".png"));}
   Console.WriteLine("CHECK "+name+" at "+scale+"x");
  }
 }
 static Form Create(string type,params object[] args){return (Form)Activator.CreateInstance(assembly.GetType("CadSignature."+type,true),args);}
 [STAThread]static int Main(string[] args){Application.EnableVisualStyles();assembly=Assembly.Load(File.ReadAllBytes(args[0]));Directory.CreateDirectory(args[1]);string scratch=Path.Combine(args[1],"isolated-profile");Directory.CreateDirectory(scratch);
  foreach(float scale in new[]{1f,1.5f,2f}){
   Audit(Create("FrameCalibEditor",new Dictionary<string,double[]>(),new Dictionary<string,double[]>(),new HashSet<string>()),"FrameCalibEditor",args[1],scale);
   var people=assembly.GetType("CadSignature.PeopleConfig",true).GetMethod("Defaults",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Invoke(null,null);
   Audit(Create("PeopleEditor",people,scratch,scratch,scratch),"PeopleEditor",args[1],scale);
   Audit(Create("SignaturePicker",scratch),"SignaturePicker",args[1],scale);
   Audit(Create("SignaturePanel","布局检查",true,true,""),"SignaturePanel",args[1],scale);
  }
  Console.WriteLine(mismatches==0?"PASS all horizontal button rows align":"FAIL misaligned rows: "+mismatches);return mismatches==0?0:1;
 }
}
