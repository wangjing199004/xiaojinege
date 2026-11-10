using System;using System.IO;using System.Reflection;using System.Collections.Generic;using System.Windows;using System.Windows.Controls;
internal static class WpfReloadTests{
 static List<Assembly> loaded=new List<Assembly>();
 [STAThread] public static int Main(string[] args){try{
 AppDomain.CurrentDomain.AssemblyResolve+=delegate(object sender,ResolveEventArgs e){var name=new AssemblyName(e.Name).Name;foreach(var a in loaded)if(a.GetName().Name==name)return a;return null;};
 foreach(var path in args){var a=Assembly.Load(File.ReadAllBytes(path));loaded.Add(a);
 var dialog=(Window)Activator.CreateInstance(a.GetType("ZwcadBatchPlot.AboutDialog",true));
 var content=(UserControl)dialog.Content;
 if(dialog.GetType().Assembly!=a||content.GetType().Assembly!=a)throw new Exception("WPF resolved an older business assembly");
 if(content.DataContext.GetType().Assembly!=a)throw new Exception("WPF viewmodel belongs to an older assembly");
 dialog.Close();Console.WriteLine("PASS: byte-loaded WPF BAML, nested control and viewmodel use "+a.GetName().Name);
 }
 if(loaded.Count!=2||loaded[0].GetName().Name==loaded[1].GetName().Name)throw new Exception("Need two independent build identities");
 return 0;}catch(Exception ex){Console.WriteLine(ex);return 1;}}
}
