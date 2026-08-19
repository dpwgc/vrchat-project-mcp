// =================================================================================================
// McpMainThreadDispatcher.cs
// Unity 主线程调度器
// -------------------------------------------------------------------------------------------------
// 背景：
//   HTTP 服务器在自己的工作线程上接收请求，但所有 UnityEditor / UnityEngine API 都必须在
//   主线程调用。本调度器负责把工具执行委托投递到主线程（EditorApplication.update）并阻塞等待结果。
//
// 行为：
//   - Post()：非阻塞投递；
//   - InvokeBlocking()：投递后阻塞等待（带超时），异常原样抛回调用线程；
//   - 域重载前清空队列，避免执行过期回调。
// =================================================================================================

using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace VrchatProjectMcp.Editor.Core
{
    /// <summary>
    /// 主线程调度器（内部静态类）。
    /// </summary>
    internal static class McpMainThreadDispatcher
    {
        /// <summary>待执行委托队列（线程安全）。</summary>
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        /// <summary>Unity 主线程 ID（类首次被触及时记录，此时必在主线程）。</summary>
        private static readonly int MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

        /// <summary>初始化：订阅主线程心跳与域重载事件。</summary>
        [InitializeOnLoadMethod]
        private static void Init()
        {
            EditorApplication.update += Drain;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        /// <summary>主线程心跳：消费队列中的全部委托。</summary>
        private static void Drain()
        {
            while (Queue.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // 非阻塞投递的委托异常无法回报调用者，统一记录
                    Debug.LogException(ex);
                }
            }
        }

        /// <summary>域重载前清空队列，避免执行失效回调。</summary>
        private static void Clear()
        {
            while (Queue.TryDequeue(out _)) { }
        }

        /// <summary>非阻塞投递一个委托到主线程。</summary>
        public static void Post(Action action)
        {
            if (action == null) return;
            Queue.Enqueue(action);
        }

        /// <summary>
        /// 阻塞等待主线程执行完成并返回结果。
        /// 超时抛出 TimeoutException；超时时刻尚未开始执行的操作会被取消，
        /// 已经在执行中的操作无法安全中断，可能仍在后台继续（会记录警告）。
        /// </summary>
        public static object InvokeBlocking(Func<object> func, int timeoutMs = 120000)
        {
            if (func == null) return null;

            // 重入防护：若调用方已经在主线程（如扩展代码从 Editor 回调中调用工具），
            // 直接同步执行，避免"主线程等待主线程"的自死锁。
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == MainThreadId) return func();

            var done = new ManualResetEventSlim(false);
            var abandoned = new AbandonFlag(); // 超时置位：阻止尚未开始的操作执行
            object result = null;
            Exception error = null;

            Queue.Enqueue(() =>
            {
                if (abandoned.Value)
                {
                    // 超时后操作才轮到执行：直接放弃，避免"迟到副作用"
                    done.Set();
                    return;
                }
                try
                {
                    result = func();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.Wait(timeoutMs))
            {
                abandoned.Value = true;
                Debug.LogWarning("[VrcProjectMCP] 主线程调用超时（" + timeoutMs + "ms）：尚未开始的操作已取消；若操作已在执行中，可能仍在后台继续");
                throw new TimeoutException("主线程执行超时（" + timeoutMs + "ms）。尚未开始的操作已取消；若操作涉及弹窗请先在编辑器中处理，已在执行中的操作可能仍在后台继续。");
            }
            if (error != null) throw error;
            return result;
        }

        /// <summary>
        /// 跨线程可见的布尔标志容器。局部变量无法声明为 volatile，
        /// 因此用持有 volatile 字段的对象来保证写线程与读线程之间的可见性。
        /// </summary>
        private sealed class AbandonFlag
        {
            public volatile bool Value;
        }
    }
}
