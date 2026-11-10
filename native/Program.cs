// ============================================================================
//  File Batch Renamer - program entry, test reporter and crash logging
//  Program.cs
//  Command line:
//     (no args)     正常启动界面；也可以把文件/文件夹拖到 exe 上启动
//     -SelfTest     无头核心自检（结果同时写入 %TEMP%\FileBatRenamer\test-SelfTest.log）
//     -UiTest       界面冒烟测试：建窗→加文件→换序→排序→预览联动，跑完自动关闭
//  Target : .NET Framework 4.x, C# 5 syntax (builds with the in-box csc.exe)
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace FileBatRenamer
{
    public class Reporter
    {
        private readonly string mode;
        private readonly List<string> lines = new List<string>();
        private int pass;
        private int fail;

        public Reporter(string mode) { this.mode = mode; }

        public void Line(string s)
        {
            lines.Add(s);
            try { Console.WriteLine(s); }
            catch { }
        }

        public void Head(string s)
        {
            Line("");
            Line(s);
        }

        public void Check(string name, bool cond, string detail)
        {
            if (cond)
            {
                pass++;
                Line("  [PASS] " + name);
            }
            else
            {
                fail++;
                Line("  [FAIL] " + name + "  " + detail);
            }
        }

        public int Finish()
        {
            Line("");
            Line((mode == "SelfTest" ? "自检结果：" : "界面冒烟测试：")
                 + "通过 " + pass + " 项，失败 " + fail + " 项");
            try
            {
                string dir = Core.TestLogDir();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "test-" + mode + ".log");
                File.WriteAllText(path, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(false));
                try { Console.WriteLine("结果已写入：" + path); }
                catch { }
            }
            catch { }
            return fail == 0 ? 0 : 1;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 有控制台时（命令行跑 -SelfTest/-UiTest）用 UTF-8 输出，避免中文乱码
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Report("界面线程异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Report("未处理异常", e.ExceptionObject as Exception);
            };

            bool selfTest = false;
            bool uiTest = false;
            bool demo = false;
            List<string> paths = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null) continue;
                if (string.Equals(a, "-SelfTest", StringComparison.OrdinalIgnoreCase)) selfTest = true;
                else if (string.Equals(a, "-UiTest", StringComparison.OrdinalIgnoreCase)) uiTest = true;
                else if (string.Equals(a, "-Demo", StringComparison.OrdinalIgnoreCase)) demo = true;
                else if (a.Length > 0) paths.Add(a);
            }

            if (selfTest)
            {
                Reporter rep = new Reporter("SelfTest");
                SelfTest.Run(rep);
                return rep.Finish();
            }

            MainForm f = new MainForm(paths.ToArray(), uiTest);
            if (demo) f.ApplyDemo();
            Application.Run(f);
            return uiTest ? f.SmokeFail : 0;
        }

        private static void Report(string title, Exception ex)
        {
            if (ex == null) return;
            string text = title + "\r\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n\r\n" + ex;
            try
            {
                string dir = Core.DefaultLogDir();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    text + "\r\n----------------------------------------\r\n", new UTF8Encoding(false));
            }
            catch { }
            try
            {
                MessageBox.Show(text, "文件批量重命名工具 - 出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
