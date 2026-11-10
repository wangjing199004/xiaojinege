param(
    [string]$OutputDirectory = '',
    [ValidateSet('All','Acad','Core','Zwcad')][string]$Platform = 'All',
    [ValidateSet('HP','Clean')][string]$Edition = 'HP'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = 'bin-update-v32-' + $Edition }
$buildId = 'r' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')
if ($Edition -eq 'Clean') {
    # Derive clean pages from current UI on every build, so shared changes cannot drift.
    $cleanViews = Join-Path $root 'src/CleanViews'
    New-Item -ItemType Directory -Force -Path $cleanViews | Out-Null
    foreach ($name in @('BatchPlotForm','RectangleBatchPlotForm','TitleBlockLibraryManagerForm')) {
        $xaml = [IO.File]::ReadAllText((Join-Path $root ('src/Common/Views/' + $name + '.xaml')))
        $handler = if ($name -eq 'TitleBlockLibraryManagerForm') { 'OnInsertStandardFrameClick' } else { 'ExportHpDirectory_Click' }
        $pattern = '\s*<Button\b[^>]*Click="' + $handler + '"[^>]*/>'
        if ([regex]::Matches($xaml, $pattern).Count -ne 1) { throw ('无法定位纯净版按钮：' + $name) }
        [IO.File]::WriteAllText((Join-Path $cleanViews ($name + '.xaml')), [regex]::Replace($xaml,$pattern,''), [Text.UTF8Encoding]::new($true))
    }
}

$targets = @(
    @{ Id='Acad'; Project='AcadBatchPlot'; Folder='AutoCAD2015-2024'; Name='AcadBatchPlot' },
    @{ Id='Core'; Project='AcadBatchPlot.Core'; Folder='AutoCAD2025-2027'; Name='AcadBatchPlot.Core' },
    @{ Id='Zwcad'; Project='BatchPlotter'; Folder='ZWCAD2025'; Name='BatchPlotter' }
)
foreach ($target in $targets) {
    if ($Platform -ne 'All' -and $Platform -ne $target.Id) { continue }
    # Build outside the installed folder to avoid replacing live signature settings and fixed loaders.
    $staging = Join-Path $root ('local-artifacts/hot-build/' + $buildId + '/' + $target.Id)
    $outputBase = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }
    $output = [IO.Path]::GetFullPath((Join-Path $outputBase $target.Folder))
    & dotnet build (Join-Path $root ('src/' + $target.Project + '/' + $target.Project + '.csproj')) -c Release -o $staging --nologo -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --ignore-failed-sources "-p:HotReloadBuildId=$buildId" "-p:ProductEdition=$Edition"
    if ($LASTEXITCODE -ne 0) { throw '业务 DLL 编译失败' }
    & dotnet build (Join-Path $root 'src/HotReload/HotReload.csproj') -c Release -o $staging --nologo -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --ignore-failed-sources "-p:CadVariant=$($target.Id)"
    if ($LASTEXITCODE -ne 0) { throw '加载器编译失败' }
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $runtime = Join-Path $staging ($target.Project + '.Runtime.' + $buildId + '.dll')
    $runtimeName = $target.Name + '.Runtime.dll'
    # Keep configuration/cache/key as a unit: copy a seed tree only on first installation.
    foreach ($entry in Get-ChildItem -LiteralPath $staging -Force) {
        if ($entry.Name -like '*.Runtime.*' -or $entry.Name -eq 'runtimes') { continue }
        $destination = Join-Path $output $entry.Name
        if ($entry.Name -eq 'SignatureData' -or $entry.Name -eq '人员设置.cspkg') {
            if (!(Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath $entry.FullName -Destination $destination -Recurse }
        } elseif ($entry.Name -like '*.Loader.dll') {
            if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $entry.FullName).Hash) {
                try { Copy-Item -LiteralPath $entry.FullName -Destination $destination -Force }
                catch { Write-Warning '固定加载器被CAD占用；首次安装或加载器变更需关闭CAD后更新。' }
            }
        } else {
            $files = if ($entry.PSIsContainer) { @(Get-ChildItem -LiteralPath $entry.FullName -Recurse -File -Force) } else { @($entry) }
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($staging.Length).TrimStart('\','/')
                $fileTarget = Join-Path $output $relative
                New-Item -ItemType Directory -Force -Path (Split-Path $fileTarget -Parent) | Out-Null
                try { Copy-Item -LiteralPath $file.FullName -Destination $fileTarget -Force }
                catch { if (!(Test-Path -LiteralPath $fileTarget)) { throw }; Write-Warning ('配套文件被占用，已保留现有文件：' + $relative) }
            }
        }
    }
    # The loaded business DLL is held in memory; update it by an atomic file replacement.
    $runtimeTarget = Join-Path $output $runtimeName
    $temporary = $runtimeTarget + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Copy-Item -LiteralPath $runtime -Destination $temporary
        if (Test-Path -LiteralPath $runtimeTarget) { [IO.File]::Replace($temporary, $runtimeTarget, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $runtimeTarget) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    Write-Host ("完成：" + $output + '；安装后由CAD启动加载，升级请关闭CAD后运行安装包。')
}
