using System; using System.IO; using System.Linq; using System.Reflection; using System.Windows; using System.Windows.Controls; using System.Windows.Media; using System.Windows.Media.Imaging; using System.Collections.Generic;
internal static class ConfigurationUiRegression
{
 static IEnumerable<DependencyObject> Walk(DependencyObject root){yield return root;foreach(object child in LogicalTreeHelper.GetChildren(root)){var item=child as DependencyObject;if(item!=null)foreach(var descendant in Walk(item))yield return descendant;}}
 [STAThread]static int Main(string[] args)
 {
  var a=Assembly.Load(File.ReadAllBytes(args[0]));var control=(UserControl)Activator.CreateInstance(a.GetType("ZwcadBatchPlot.EnvironmentConfigurationControl",true),true);
  var boxes=Walk(control).OfType<CheckBox>().ToList();if(boxes.Count!=4||boxes.Any(b=>b.IsChecked!=true))throw new Exception("Four categories must be selected by default");
  var buttons=Walk(control).OfType<Button>().ToList();buttons.Single(b=>(string)b.Content=="取消全选").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));if(boxes.Any(b=>b.IsChecked==true))throw new Exception("Deselect all failed");
  boxes[1].IsChecked=true;int selection=Convert.ToInt32(control.GetType().GetProperty("Selected",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(control,null));if(selection!=2)throw new Exception("Library selection mask incorrect");
  buttons.Single(b=>(string)b.Content=="全选").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));if(boxes.Any(b=>b.IsChecked!=true))throw new Exception("Select all failed");
  control.Background=Brushes.White;control.Width=760;control.Height=540;control.Measure(new Size(760,540));control.Arrange(new Rect(0,0,760,540));control.UpdateLayout();
  var bitmap=new RenderTargetBitmap(760,540,96,96,PixelFormats.Pbgra32);bitmap.Render(control);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(args[1]))encoder.Save(stream);
  Console.WriteLine("PASS 4 configuration checkboxes, select/deselect all, per-category selection mask and UI render");return 0;
 }
}
