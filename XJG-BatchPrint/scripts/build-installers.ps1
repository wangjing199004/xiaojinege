param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$payloadRoot = Join-Path $root 'local-artifacts/installer-v32'
$output = Join-Path $root '安装包-v32'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
New-Item -ItemType Directory -Force -Path $output | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$manifestPath = Join-Path $payloadRoot 'setup.manifest'
New-Item -ItemType Directory -Force -Path $payloadRoot | Out-Null
[IO.File]::WriteAllText($manifestPath, @'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="32.0.0.0" name="DrawingCenter.Setup" processorArchitecture="amd64" type="win32"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo>
</assembly>
'@, [Text.UTF8Encoding]::new($false))
$references = @('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Core.dll','/r:System.Xml.Linq.dll','/r:System.Xml.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$source = @( (Join-Path $root 'src/Installer/Setup.cs'), (Join-Path $root 'src/Installer/CadStartupRegistration.cs'), (Join-Path $root 'src/Installer/UninstallWorkflow.cs') )
$uninstaller = Join-Path $payloadRoot 'uninstall.exe'
& $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /define:UNINSTALL_ONLY "/win32manifest:$manifestPath" "/out:$uninstaller" $references $source
if ($LASTEXITCODE -ne 0) { throw '卸载程序编译失败' }
foreach ($edition in @('HP','Clean')) {
    $payload = Join-Path $payloadRoot $edition
    if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'build-hot-update.ps1') -Edition $edition -OutputDirectory $payload }
    $zipPath = Join-Path $payloadRoot ($edition + '.zip')
    $xml = [xml]'<payload />'
    $packageSources = @{}
    foreach ($platform in @('AutoCAD2015-2024','AutoCAD2025-2027','ZWCAD2025')) {
        $folder = Join-Path $payload $platform
        $readme = "出图中心 v32 " + $edition + "`r`n`r`n安装程序已注册对应CAD启动加载，正常启动CAD即可使用「出图中心」。`r`n已安装其他版本时，结束CAD后安装替换，避免同时加载两个版本。`r`nHP版包含全部功能；纯净版移除生成HP目录、插入标准图框及相关模板资源。普通目录和图框签名等功能保留。`r`n个人配置、签名数据在升级及卸载时保留。`r`n设置>日志右侧的「配置管理」二级页提供分类复选框及配置导出/导入，支持图框库、签名标定、名单、笔迹及插件设置迁移；导入前自动备份。新生成的目录图名正文左对齐，距表格左线一个字高。`r`n"
        [IO.File]::WriteAllText((Join-Path $folder '安装使用说明.txt'), $readme, [Text.UTF8Encoding]::new($true))
        # 始终从工厂配置取种子，禁止把输出目录中实际使用过的名单/缓存/笔迹打包。
        $programFiles = @(Get-ChildItem -LiteralPath $folder -Recurse -File -Force | Where-Object {
            $relative = $_.FullName.Substring($folder.Length+1).Replace('\','/')
            $privateExtensions = @('.pdb','.cspkg','.dcenv','.cache','.key','.csig','.log','.bak','.tmp')
            $privateNames = @('Settings.json','TitleBlockLibrary.json','人员名单.txt','图框定位.txt','签名文件夹.txt')
            $_.Extension -notin $privateExtensions -and $_.Name -notin $privateNames -and !$relative.StartsWith('SignatureData/',[StringComparison]::OrdinalIgnoreCase) -and !$relative.StartsWith('Signatures/',[StringComparison]::OrdinalIgnoreCase) -and !$relative.StartsWith('.session/',[StringComparison]::OrdinalIgnoreCase)
        })
        foreach ($file in $programFiles) {
            $node = $xml.CreateElement('file')
            $node.SetAttribute('platform',$platform)
            $node.SetAttribute('path',$file.FullName.Substring($folder.Length+1).Replace('\','/'))
            $node.SetAttribute('sha256',(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)
            $xml.DocumentElement.AppendChild($node) | Out-Null
            $packageSources[$platform + '/' + $node.GetAttribute('path')] = $file.FullName
        }
        $factory = Join-Path $root 'resources/frame-tools-factory'
        foreach ($file in Get-ChildItem -LiteralPath $factory -Recurse -File -Force) {
            $relative = $file.FullName.Substring($factory.Length+1).Replace('\','/')
            $node = $xml.CreateElement('file'); $node.SetAttribute('platform',$platform); $node.SetAttribute('path',$relative)
            $node.SetAttribute('sha256',(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)
            $xml.DocumentElement.AppendChild($node) | Out-Null
            $packageSources[$platform + '/' + $relative] = $file.FullName
        }
    }
    $stream = [IO.File]::Open($zipPath,[IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
    try {
        foreach ($node in $xml.DocumentElement.ChildNodes) {
            $file = $packageSources[$node.GetAttribute('platform') + '/' + $node.GetAttribute('path')]
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file,($node.GetAttribute('platform') + '/' + $node.GetAttribute('path')),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        $entry = $zip.CreateEntry('payload.xml')
        $writer = [IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false))
        try { $writer.Write($xml.OuterXml) } finally { $writer.Dispose() }
    } finally { $zip.Dispose(); $stream.Dispose() }
    $name = if ($edition -eq 'HP') { '出图中心-HP版-v32-安装.exe' } else { '出图中心-纯净版-v32-安装.exe' }
    $flags = if ($edition -eq 'Clean') { @('/define:CLEAN_EDITION') } else { @() }
    & $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ "/win32manifest:$manifestPath" "/out:$(Join-Path $output $name)" "/resource:$zipPath,payload.zip" "/resource:$uninstaller,uninstall.exe" $flags $references $source
    if ($LASTEXITCODE -ne 0) { throw ('安装程序编译失败：' + $edition) }
}
Get-ChildItem -LiteralPath $output -Filter '*.exe' | ForEach-Object { [PSCustomObject]@{ File=$_.Name; MB=[Math]::Round($_.Length/1MB,2); SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash } } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'SHA256.json') -Encoding UTF8
Write-Host ('安装包已生成：' + $output)
