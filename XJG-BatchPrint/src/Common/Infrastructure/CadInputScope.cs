using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZwcadBatchPlot;

/// <summary>
/// 标记「当前正在 CAD 绘图区取点」的时间窗，供命令层的等待循环识别。
///
/// 背景：非模态 WPF 窗显示后，命令层用 <c>Dispatcher.PushFrame</c> 等待窗体关闭。
/// 但 GetPoint/GetCorner 是在这个**嵌套 Dispatcher 帧**里执行的，
/// AutoCAD 的对象捕捉依赖 CAD 自身消息循环做拾取命中测试；嵌套帧期间
/// 块内图元（BlockReference 内部实体）的拾取路径不会被求值，
/// 表现为「单独线能捕捉、块内线完全捕捉不到」。
///
/// 做法：<see cref="BeginCadPick"/> 开始取点时置 <see cref="IsActive"/>，
/// <see cref="EndCadPick"/> 结束时清掉。命令层等待循环据此在取点期间
/// 暂时退掉嵌套帧、改用纯 <c>Application.DoEvents()</c>，把消息循环交还 CAD。
/// 取点结束恢复帧泵，保证 WPF 按钮点击与重绘正常。
///
/// 纯标志位，不含任何数据库或窗体操作。
/// </summary>
internal static class CadInputScope
{
    private static int _active;

    /// <summary>取点期间为 true。</summary>
    public static bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>进入取点状态。</summary>
    public static void Enter() => Interlocked.Increment(ref _active);

    /// <summary>退出取点状态（可重入，用计数配平）。</summary>
    public static void Exit()
    {
        // 不允许减到负数：Enter/Exit 必须配平，异常路径下宁可保持 active 也不能错乱。
        if (Volatile.Read(ref _active) > 0)
        {
            Interlocked.Decrement(ref _active);
        }
    }
}