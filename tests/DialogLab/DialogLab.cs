using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

internal static class DialogLab
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: DialogLab.exe <WorkspaceRestorer.exe>");
            Environment.Exit(2);
        }

        Assembly product = Assembly.LoadFrom(args[0]);
        Application app = new Application();
        Type theme = product.GetType("WorkspaceRestorer.UI.AppTheme", true);
        theme.GetMethod("Apply", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { app });

        Window owner = new Window
        {
            Title = "DialogLab Owner",
            Width = 900,
            Height = 600,
            Left = 80,
            Top = 80,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            Background = System.Windows.Media.Brushes.White
        };
        owner.Show();
        owner.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ApplicationIdle);

        Type dialog = product.GetType("WorkspaceRestorer.UI.Shared.ConfirmDialog", true);
        MethodInfo ask = dialog.GetMethod(
            "Ask",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new Type[] { typeof(Window), typeof(string), typeof(string), typeof(string) },
            null);
        if (ask == null)
        {
            Console.Error.WriteLine("ConfirmDialog.Ask(Window,string,string,string) not found.");
            Environment.Exit(3);
        }

        string frameDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "csk-dialoglab", "frames");
        if (System.IO.Directory.Exists(frameDir))
        {
            System.IO.Directory.Delete(frameDir, true);
        }
        System.IO.Directory.CreateDirectory(frameDir);

        bool prewarm = args.Length >= 2 && string.Equals(args[1], "--prewarm", StringComparison.OrdinalIgnoreCase);
        bool gate = args.Length >= 2 && string.Equals(args[1], "--gate", StringComparison.OrdinalIgnoreCase);
        bool noDwm = args.Length >= 2 && string.Equals(args[1], "--gate-nodwm", StringComparison.OrdinalIgnoreCase);
        if (args.Length >= 2 && string.Equals(args[1], "--prewarm-gate", StringComparison.OrdinalIgnoreCase))
        {
            prewarm = true;
            gate = true;
        }
        if (args.Length >= 2 && string.Equals(args[1], "--prewarm-gate-nodwm", StringComparison.OrdinalIgnoreCase))
        {
            prewarm = true;
            gate = true;
            noDwm = true;
        }
        if (gate || noDwm)
        {
            gate = true;
        }
        if (prewarm)
        {
            PrewarmConfirmWindow(dialog, owner);
        }

        for (int round = 1; round <= 2; round++)
        {
            DialogSampler sampler = new DialogSampler("删除到回收站", frameDir, round);
            sampler.Start();
            Thread closer = new Thread(delegate()
            {
                Thread.Sleep(2200);
                sampler.CloseDialog();
            });
            closer.IsBackground = true;
            closer.Start();

            object result;
            if (gate)
            {
                result = ShowGated(dialog, owner, noDwm);
            }
            else
            {
                result = ask.Invoke(null, new object[]
                {
                    owner,
                    "删除到回收站",
                    "确定删除工作方案“测试方案”吗？",
                    "它会先放进回收站（保留 5 天，可恢复）。"
                });
            }

            sampler.Stop();
            Console.WriteLine("==== ROUND " + round + " result=" + result + " ====");
            sampler.Dump();
            Thread.Sleep(700);
        }

        owner.Close();
        app.Shutdown(0);
    }

    private static bool ShowGated(Type dialog, Window owner, bool noDwm)
    {
        Type windowType = dialog.GetNestedType("ConfirmWindow", BindingFlags.NonPublic);
        ConstructorInfo constructor = windowType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new Type[] { typeof(string), typeof(string), typeof(string) },
            null);
        Window window = (Window)constructor.Invoke(new object[]
        {
            "删除到回收站",
            "确定删除工作方案“测试方案”吗？",
            "它会先放进回收站（保留 5 天，可恢复）。"
        });
        window.Owner = owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Opacity = 0.0;
        if (noDwm)
        {
            window.SourceInitialized += delegate
            {
                int disabled = 1;
                IntPtr handle = new WindowInteropHelper(window).Handle;
                DwmSetWindowAttribute(handle, 3, ref disabled, sizeof(int));
            };
        }
        window.ContentRendered += delegate
        {
            window.Dispatcher.BeginInvoke(
                new Action(delegate
                {
                    window.Opacity = 1.0;
                }),
                DispatcherPriority.Render);
        };
        return window.ShowDialog() == true;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private static void PrewarmConfirmWindow(Type dialog, Window owner)
    {
        Type windowType = dialog.GetNestedType("ConfirmWindow", BindingFlags.NonPublic);
        ConstructorInfo constructor = windowType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new Type[] { typeof(string), typeof(string), typeof(string) },
            null);
        Window window = (Window)constructor.Invoke(new object[] { "预热", "预热", "预热" });
        window.Opacity = 0.0;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;

        DispatcherFrame frame = new DispatcherFrame();
        DispatcherTimer timer = new DispatcherTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += delegate
        {
            timer.Stop();
            frame.Continue = false;
        };
        window.ContentRendered += delegate
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        window.Show();
        Dispatcher.PushFrame(frame);
        window.Close();
    }
}

internal sealed class DialogSampler
{
    private const uint WmClose = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private struct Sample
    {
        public double Ms;
        public bool Visible;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public uint ForegroundPid;
        public ulong FrameHash;
        public int AverageBrightness;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private readonly string title;
    private readonly string frameDir;
    private readonly int round;
    private readonly int processId;
    private readonly List<Sample> samples = new List<Sample>();
    private readonly HashSet<ulong> savedFrames = new HashSet<ulong>();
    private Thread thread;
    private volatile bool running;
    private IntPtr dialogHandle;
    private int frameIndex;

    public DialogSampler(string title, string frameDir, int round)
    {
        this.title = title;
        this.frameDir = frameDir;
        this.round = round;
        this.processId = Process.GetCurrentProcess().Id;
    }

    public void Start()
    {
        running = true;
        thread = new Thread(Run);
        thread.IsBackground = true;
        thread.Start();
    }

    public void Stop()
    {
        running = false;
        if (thread != null)
        {
            thread.Join(3000);
        }
    }

    public void CloseDialog()
    {
        IntPtr handle = dialogHandle;
        if (handle != IntPtr.Zero && IsWindow(handle))
        {
            PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Dump()
    {
        for (int index = 0; index < samples.Count; index++)
        {
            Sample sample = samples[index];
            Console.WriteLine(string.Format(
                "{0,8:0.0} ms  visible={1,-5}  rect=({2},{3})-({4},{5})  size={6}x{7}  fgPid={8}  frame={9:x16}  brightness={10}",
                sample.Ms,
                sample.Visible,
                sample.Left,
                sample.Top,
                sample.Right,
                sample.Bottom,
                sample.Right - sample.Left,
                sample.Bottom - sample.Top,
                sample.ForegroundPid,
                sample.FrameHash,
                sample.AverageBrightness));
        }
        Console.WriteLine("samples=" + samples.Count + " frames=" + frameIndex);
    }

    private void Run()
    {
        timeBeginPeriod(1);
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            bool hasPrevious = false;
            Sample previous = new Sample();
            double nextFrameAt = 0.0;
            while (running)
            {
                if (dialogHandle == IntPtr.Zero || !IsWindow(dialogHandle))
                {
                    dialogHandle = FindDialog();
                }

                Sample current = new Sample();
                current.Ms = watch.Elapsed.TotalMilliseconds;
                current.Visible = dialogHandle != IntPtr.Zero && IsWindowVisible(dialogHandle);
                if (dialogHandle != IntPtr.Zero)
                {
                    RECT rect;
                    if (GetWindowRect(dialogHandle, out rect))
                    {
                        current.Left = rect.Left;
                        current.Top = rect.Top;
                        current.Right = rect.Right;
                        current.Bottom = rect.Bottom;
                    }
                }
                IntPtr foreground = GetForegroundWindow();
                uint foregroundPid;
                GetWindowThreadProcessId(foreground, out foregroundPid);
                current.ForegroundPid = foregroundPid;

                if (current.Visible && current.Ms >= nextFrameAt)
                {
                    nextFrameAt = current.Ms + 25.0;
                    CaptureFrame(ref current);
                }

                if (!hasPrevious
                    || current.Visible != previous.Visible
                    || current.Left != previous.Left
                    || current.Top != previous.Top
                    || current.Right != previous.Right
                    || current.Bottom != previous.Bottom
                    || current.ForegroundPid != previous.ForegroundPid
                    || current.FrameHash != previous.FrameHash)
                {
                    samples.Add(current);
                    previous = current;
                    hasPrevious = true;
                }

                Thread.Sleep(1);
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    private void CaptureFrame(ref Sample sample)
    {
        int width = sample.Right - sample.Left;
        int height = sample.Bottom - sample.Top;
        if (width <= 0 || height <= 0 || width > 4000 || height > 4000)
        {
            return;
        }
        try
        {
            using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        sample.Left,
                        sample.Top,
                        0,
                        0,
                        new System.Drawing.Size(width, height),
                        CopyPixelOperation.SourceCopy);
                }
                ulong hash;
                int brightness;
                HashBitmap(bitmap, out hash, out brightness);
                sample.FrameHash = hash;
                sample.AverageBrightness = brightness;
                if (savedFrames.Add(hash))
                {
                    frameIndex++;
                    string path = System.IO.Path.Combine(
                        frameDir,
                        string.Format("r{0}-{1:000}-{2:0000}ms.png", round, frameIndex, (int)sample.Ms));
                    bitmap.Save(path, ImageFormat.Png);
                }
            }
        }
        catch
        {
        }
    }

    private static void HashBitmap(Bitmap bitmap, out ulong hash, out int brightness)
    {
        hash = 1469598103934665603UL;
        long sum = 0;
        int count = 0;
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* pointer = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < bitmap.Height; y += 2)
                {
                    byte* row = pointer + y * stride;
                    for (int x = 0; x < bitmap.Width; x += 2)
                    {
                        int offset = x * 4;
                        byte b = row[offset];
                        byte g = row[offset + 1];
                        byte r = row[offset + 2];
                        hash ^= r;
                        hash *= 1099511628211UL;
                        hash ^= g;
                        hash *= 1099511628211UL;
                        hash ^= b;
                        hash *= 1099511628211UL;
                        sum += (r * 30 + g * 59 + b * 11) / 100;
                        count++;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        brightness = count == 0 ? -1 : (int)(sum / count);
    }

    private IntPtr FindDialog()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr handle, IntPtr lParam)
        {
            uint pid;
            GetWindowThreadProcessId(handle, out pid);
            if (pid != (uint)processId)
            {
                return true;
            }
            StringBuilder text = new StringBuilder(256);
            GetWindowText(handle, text, text.Capacity);
            if (text.ToString().IndexOf(title, StringComparison.Ordinal) >= 0)
            {
                found = handle;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
