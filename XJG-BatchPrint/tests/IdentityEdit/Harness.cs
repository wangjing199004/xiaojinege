using System;using System.Collections.Generic;using System.Collections.ObjectModel;using System.Linq;using System.Windows;using System.Windows.Controls;using System.Windows.Data;using System.Windows.Media;using System.Windows.Threading;
public class PlotJob {public string Title{get;set;}="original";public string DrawingNumber{get;set;}="01";public string CadTitle{get;set;}="original";public string CadDrawingNumber{get;set;}="01";public long SortPriority{get;set;}public string SourceFile{get;set;}="external.dwg";}
static class CadTextUpdater {public static bool TryUpdateOpenDocument(PlotJob job,string? title,string? number,object doc,out string message){message="checked";return job.SourceFile=="open.dwg";}}
public partial class Harness : Window
{
 readonly DataGrid _grid=new(){AutoGenerateColumns=false,CanUserAddRows=false};readonly DataGridTextColumn _titleColumn=new(){Header="图名",Binding=new Binding("Title"){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged}};readonly DataGridTextColumn _drawingNumberColumn=new(){Header="图号",Binding=new Binding("DrawingNumber"){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged}};
 readonly ObservableCollection<PlotJob> _jobs=new();readonly object _currentDocument=new();readonly TextBlock _statusLabel=new();bool _closed=false;bool inEnding;int refreshes;int updates;
 void AppendLog(string level,string text){updates++;}
 void SortAndRefreshOutputPaths(){if(inEnding)throw new Exception("Refreshed during commit");if(++refreshes>100)throw new Exception("Repeated commit loop");var rows=_jobs.OrderBy(j=>j.DrawingNumber).ToArray();_jobs.Clear();foreach(var row in rows)_jobs.Add(row);_grid.Items.Refresh();}
 bool IsDrawingIdentityColumn(DataGridColumn column)=>column==_titleColumn||column==_drawingNumberColumn;
 static IEnumerable<DependencyObject> Walk(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Walk(VisualTreeHelper.GetChild(root,i)))yield return child;}
 void Pump(){Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(()=>{}));UpdateLayout();}
 void Edit(PlotJob row,DataGridColumn column,string value,bool cancel=false){_grid.CurrentCell=new DataGridCellInfo(row,column);_grid.ScrollIntoView(row,column);_grid.BeginEdit();UpdateLayout();var box=Walk(_grid).OfType<TextBox>().Single();box.Text=value;if(cancel)_grid.CancelEdit(DataGridEditingUnit.Cell);else _grid.CommitEdit(DataGridEditingUnit.Cell,true);Pump();}
 [STAThread]public static void Main(){var h=new Harness{Width=600,Height=350,Left=-30000,Top=-30000,ShowInTaskbar=false};h.Content=h._grid;h._grid.Columns.Add(h._drawingNumberColumn);h._grid.Columns.Add(h._titleColumn);var external=new PlotJob();var open=new PlotJob{SourceFile="open.dwg"};h._jobs.Add(external);h._jobs.Add(open);h._grid.ItemsSource=h._jobs;h._grid.CellEditEnding+=(s,e)=>{h.inEnding=true;try{h.Grid_CellEditEnding(s,e);}finally{h.inEnding=false;}};h.Show();h.Pump();
 for(int i=0;i<20;i++)h.Edit(external,h._titleColumn,"external-"+i);if(external.Title!="external-19"||external.CadTitle!="original"||h.refreshes!=20||h.updates!=20)throw new Exception("External edits lost or repeated");
 h.Edit(open,h._drawingNumberColumn,"99");if(open.CadDrawingNumber!="99"||h.refreshes!=21)throw new Exception("Open drawing writeback baseline not updated");
 int before=h.updates;h.Edit(external,h._titleColumn,"cancel",true);if(h.updates!=before)throw new Exception("Cancel triggered CAD update");
 h.Close();Console.WriteLine("PASS source-backed real WPF editing: 20 external edits without recursion, deferred refresh only after commit, opened-drawing baseline updated, cancel performs no CAD update");}
}
