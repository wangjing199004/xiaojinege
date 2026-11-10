using System; using System.IO; using System.Reflection; using System.Drawing; using System.Windows.Forms;
internal static class UiSnapshot
{
    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var assembly=Assembly.Load(File.ReadAllBytes(args[0]));
        using(var form=(Form)Activator.CreateInstance(assembly.GetType("SetupForm",true),true))
        {
            form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-30000,-30000); form.Show(); form.PerformLayout(); Application.DoEvents();
            using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height)); bitmap.Save(args[1]); }
        }
        return 0;
    }
}
