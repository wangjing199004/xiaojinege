using System;
using System.Drawing;
using System.Windows.Forms;
using ZwcadBatchPlot;
internal static class GridHeaders
{
    [STAThread] static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        foreach (float factor in new[] { 1f, 1.5f, 2f, 3f })
        using (var form = new Form { Width = 1000, Height = 500, ShowInTaskbar = false, Location = new Point(-30000, -30000), StartPosition = FormStartPosition.Manual })
        using (var font = new Font("Microsoft YaHei UI", 9 * factor))
        using (var grid = new DataGridView())
        {
            UiLayout.StyleFrameToolsGrid(grid, font);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "写入", Width = 50 });
            foreach (var header in new[] { "属性标记", "属性内容（可编辑）", "文件名", "文件路径", "处理结果 / 备份位置" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            form.Controls.Add(grid); form.Show(); Application.DoEvents();
            grid.Rows.Add(true, "图号", "测试内容", "测试.dwg", "D:/图纸/测试.dwg", "待处理");
            foreach (int width in new[] { 1000, 480 })
            {
                form.Width = width; Application.DoEvents();
                using (var graphics = grid.CreateGraphics()) foreach (DataGridViewColumn column in grid.Columns)
                {
                    var measured = TextRenderer.MeasureText(graphics, column.HeaderText, grid.ColumnHeadersDefaultCellStyle.Font, Size.Empty, TextFormatFlags.SingleLine);
                    if (column.Width < measured.Width || grid.ColumnHeadersHeight < measured.Height) throw new Exception("Header clipped at " + factor + "x: " + column.HeaderText);
                }
            }
            if (grid.EnableHeadersVisualStyles || grid.ColumnHeadersDefaultCellStyle.BackColor != Color.FromArgb(245,247,250) || !grid.ColumnHeadersDefaultCellStyle.Font.Bold)
                throw new Exception("Scan table header style mismatch");
            if (factor == 2f) { form.Width = 1000; Application.DoEvents(); using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height)); bitmap.Save(args[0]); } }
            Console.WriteLine("PASS " + factor + "x font-scale: all six headers fit at normal/narrow widths; scan header style matches");
        }
    }
}
