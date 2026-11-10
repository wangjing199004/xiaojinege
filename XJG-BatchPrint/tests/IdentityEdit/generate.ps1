param([string]$GeneratedFolder)
$sourcePath = Join-Path $PSScriptRoot '../../src/Common/Views/BatchPlotForm.xaml.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$start = $source.IndexOf('    private readonly Dictionary<PlotJob, (bool Title, bool Number)>')
$end = $source.IndexOf('    private bool IsDrawingIdentityColumn', $start)
if ($start -lt 0 -or $end -lt 0) { throw '找不到生产代码编辑处理器' }
$target = Join-Path $PSScriptRoot $GeneratedFolder
New-Item -ItemType Directory -Force -Path $target | Out-Null
[IO.File]::WriteAllText((Join-Path $target 'ActualHandlers.g.cs'), "#nullable enable`nusing System;using System.Collections.Generic;using System.Linq;using System.Windows.Controls;using System.Windows.Threading;`npublic partial class Harness {`n" + $source.Substring($start,$end-$start) + "`n}")
