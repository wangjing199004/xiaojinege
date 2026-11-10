using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
internal static class GridHeaders
{
 public sealed class Item { public int Number {get;set;}=1; public bool Selected {get;set;}=true; public string FileName {get;set;}="测试文件.dwg"; public string[] PaperOptions {get;set;}=new[]{"A3 / 1:100"}; public string PaperChoice {get;set;}="A3 / 1:100"; }
 static IEnumerable<DependencyObject> Walk(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Walk(VisualTreeHelper.GetChild(root,i)))yield return child;}
 static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
 [STAThread]static void Main(string[] args){
  XNamespace p="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
  var source=XDocument.Load(Path.Combine(args[0],"src/Common/Views/RectangleBatchPlotForm.xaml"));
  var gridXml=new XElement(source.Descendants(p+"DataGrid").Single());
  var events=new[]{"Sorting","ContextMenuOpening","PreviewMouseLeftButtonDown","PreviewMouseRightButtonDown","SelectionChanged","CurrentCellChanged","Click"};
  foreach(var element in gridXml.DescendantsAndSelf())foreach(var attribute in element.Attributes().Where(a=>events.Contains(a.Name.LocalName)).ToArray())attribute.Remove();
  var dictionary=XDocument.Load(Path.Combine(args[0],"src/Common/Views/WpfUi.xaml")).Root!;
  var wrapper=new XElement(p+"Grid",new XElement(p+"Grid.Resources",dictionary),gridXml);
  foreach(double scale in new[]{1d,1.5d,2d}){
   var root=(Grid)XamlReader.Parse(wrapper.ToString());root.LayoutTransform=new ScaleTransform(scale,scale);
   var grid=root.Children.OfType<DataGrid>().Single();
   var window=new Window{Width=1000,Height=450,Content=root,ShowInTaskbar=false,Left=-30000,Top=-30000};window.Show();window.UpdateLayout();
   foreach(double width in new[]{1000d,500d,1200d}){
    window.Width=width;window.UpdateLayout();
    double[] expected={52,64,58};for(int i=0;i<3;i++)Check(Math.Abs(grid.Columns[i].ActualWidth-expected[i])<1,"Empty column compressed: "+grid.Columns[i].Header);
    Check(grid.Columns.All(c=>!c.Width.IsStar),"Star widths still compete with manual resize");
   }
   var headers=Walk(grid).OfType<DataGridColumnHeader>().Where(h=>h.Column!=null).ToArray();
   int sorts=0;grid.Sorting+=(s,e)=>{sorts++;e.Handled=true;};
   var paper=headers.Single(h=>(string)h.Column.Header=="纸张尺寸 / 比例");
   typeof(DataGridColumnHeader).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(paper,null);
   Check(sorts==1,"Template header native Sorting event failed");
   var header=headers.Single(h=>(string)h.Column.Header=="预览");
   var thumb=Walk(header).OfType<Thumb>().Single(t=>t.Name=="PART_RightHeaderGripper");double before=header.Column.ActualWidth;
   thumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
   thumb.RaiseEvent(new DragDeltaEventArgs(35,0){RoutedEvent=Thumb.DragDeltaEvent});
   thumb.RaiseEvent(new DragCompletedEventArgs(35,0,false){RoutedEvent=Thumb.DragCompletedEvent});window.UpdateLayout();
   Check(header.Column.ActualWidth>before+30,"Native column resize failed");Check(sorts==1,"Resize unexpectedly sorted");
   grid.ItemsSource=new[]{new Item()};window.UpdateLayout();
   Check(header.Column.ActualWidth>before+30,"Loading data reset manually resized width");
   if(scale==1){grid.ItemsSource=null;window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(args[1]))encoder.Save(output);}
   window.Close();Console.WriteLine("PASS "+scale+"x layout: empty headers at narrow/wide widths, native template sorting, resize without sorting, width retained after data load");
  }
 }
}
