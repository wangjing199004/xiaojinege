#nullable disable
#if AUTOCAD
#if ACAD_CORE
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
using CadDatabase = Autodesk.AutoCAD.DatabaseServices.Database;
using CadRuntime = Autodesk.AutoCAD.Runtime;
#else
using AcApp = ZwSoft.ZwCAD.ApplicationServices.Application;
using CadDatabase = ZwSoft.ZwCAD.DatabaseServices.Database;
using CadRuntime = ZwSoft.ZwCAD.Runtime;
#endif
using System.IO;
using System.Reflection;

namespace CadSignature
{
    /// <summary>
    /// 核心程序集对外的唯一入口。外壳 CadSignature.dll（Source\Loader）用
    /// <c>Assembly.Load(byte[])</c> 把本程序集读进内存后，所有命令都从这里进来。
    ///
    /// 这里有两条规矩：
    /// 1. **方法名不能改** —— 外壳是按名字反射查找的（ShowPanel / Setup / Sign / Clear）；
    /// 2. **本类里不要出现 AutoCAD 类型** —— 外壳要在调用 Initialize/读 Version 之前就把本类
    ///    加载出来，保持它不依赖 AutoCAD 可以让载入过程更稳。
    ///
    /// 日常改功能请改 Commands.cs / SignaturePanel.cs，这里通常不用动。
    /// </summary>
    public static class CoreEntry
    {
        /// <summary>核心 DLL 所在目录（装机包的 Contents\Windows），由外壳在每次载入后注入。
        /// 用 <c>Assembly.Load(byte[])</c> 载入的程序集没有磁盘路径（Location 是空串），
        /// 必须靠它才能往上找到 Signatures 签名素材。</summary>
        public static string BaseDirectory = "";
        // The unified plugin keeps exported packages outside the encrypted session directory.
        internal static string PackageBaseDirectory = "";

        /// <summary>由外壳调用，注入本程序集所在目录。不要在这里做别的初始化。</summary>
        public static void Initialize(string baseDirectory)
        {
            BaseDirectory = baseDirectory ?? "";
        }

        /// <summary>本程序集**物理文件**所在目录（5.26.0）。
        ///
        /// 和 <see cref="BaseDirectory"/> 的区别在于绿色版：
        ///   * **单文件绿色版** —— 核心就编在这个 DLL 里、是用户 NETLOAD 进来的，<c>Location</c>
        ///     有值，于是这里 = **用户放 DLL 的那个文件夹**；而 <c>BaseDirectory</c> 被
        ///     GreenStore 指到了运行时的工作目录 <c>.session</c>（命令一结束就被删掉）。
        ///   * **装机版** —— 核心是被 <c>Assembly.Load(byte[])</c> 读进内存的，<c>Location</c>
        ///     是空串，退回 <see cref="BaseDirectory"/>（Contents\Windows）。
        ///
        /// 「人员设置.cspkg」配置包就认这个目录：**放在 DLL 旁边**（换台电脑 / 发给同事时
        /// 它自然跟着走），而不是放在那个用完即删的工作目录里。</summary>
        public static string PackageDirectory
        {
            get
            {
                if (!string.IsNullOrEmpty(PackageBaseDirectory)) return PackageBaseDirectory;
                try
                {
                    string location = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(location))
                    {
                        string folder = Path.GetDirectoryName(location);
                        if (!string.IsNullOrEmpty(folder)) return folder;
                    }
                }
                catch (System.Exception)
                {
                }
                return BaseDirectory;
            }
        }

        /// <summary>核心版本号（取自程序集版本）。显示在签字面板标题和命令行提示里，
        /// 用来一眼确认 AutoCAD 里跑的是不是刚编译出来的那一份。</summary>
        public static string Version
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
        }

        /// <summary>打开签字面板（签字范围 / 一键签名 / 隐藏签名 / 清除签名）。</summary>
        public static void ShowPanel() { new Commands().ShowSignaturePanel(); }

        /// <summary>签字范围：框选签字栏建立定位点。</summary>
        public static void Setup() { new Commands().SetupA1SignatureSlots(); }

        /// <summary>一键签名。</summary>
        public static void Sign() { new Commands().ApplyA1Signatures(); }

        /// <summary>清除签名。</summary>
        public static void Clear() { new Commands().ClearA1Signatures(); }
    }
}
