# HP 目录插入宿主回归

HostRegression.cs 为独立 AutoCAD 控制台命令 HP_TEST。
使用 .NET Framework csc，引用本机 AutoCAD2024 的 AcCoreMgd、AcDbMgd、AcMgd 编译测试DLL，NETLOAD后执行 HP_TEST。
环境变量 XJG_HP_TEST_ROOT 可指定工作区，默认原工作区路径。测试读取 bin-update-v18/AutoCAD2015-2024/AcadBatchPlot.dll ，实际生成使用DLL内置加密模板；“目录”中的A3/A4源模板仅用于独立比对属性位置。
每个用例使用独立测试数据库，输出 local-artifacts/hp-template-inspect/insert-行数.dwg，不修改用户图纸和模板。

检查1/27/28/59/60/86/118/119行的插入：分页数量、所有序号（含空白行）、连续顺序、过滤未勾选项、A3 32+27双栏、块外DBText、单行长文字宽度、特殊字符、7个标题栏属性实例值和默认值清空且锚点随块移动、左上角插入基点、图层锁恢复、重复插入独立块定义。
未覆盖完整WPF按钮/鼠标取点，且不代表所有CAD平台均已完成宿主验证。
